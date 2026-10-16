"""Dependency-free worker checks: no models, network, or real meeting audio."""
import ast
import importlib.util
import json
from pathlib import Path
import socket
import tempfile
import unittest
from unittest.mock import Mock, patch
import weakref

source = Path(__file__).resolve().parents[1] / 'src/Recap/LocalWhisper/worker.py'
spec = importlib.util.spec_from_file_location('recap_worker', source)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class WorkerTests(unittest.TestCase):
    def test_alignment_always_uses_cpu(self):
        tree = ast.parse(source.read_text(encoding='utf-8'))
        run = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'run')
        assignments = [node for node in ast.walk(run) if isinstance(node, ast.Assign)
                       and any(isinstance(target, ast.Name) and target.id == 'alignment_device' for target in node.targets)]
        self.assertEqual(len(assignments), 1)
        self.assertEqual(ast.literal_eval(assignments[0].value), 'cpu')
        checked = set()
        for node in ast.walk(run):
            if not isinstance(node, ast.Call):
                continue
            name = node.func.id if isinstance(node.func, ast.Name) else node.func.attr if isinstance(node.func, ast.Attribute) else ''
            argument = {'load_danish_alignment': 1, 'load_align_model': 1, 'align': 4}.get(name)
            if argument is not None:
                self.assertIsInstance(node.args[argument], ast.Name)
                self.assertEqual(node.args[argument].id, 'alignment_device')
                checked.add(name)
            if name == 'load_model':
                self.assertEqual(node.args[1].id, 'device')
        self.assertEqual(checked, {'load_danish_alignment', 'load_align_model', 'align'})

    def test_checkpoint_reuse_and_invalidation(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'checkpoint.json'
            self.assertEqual(worker.checkpoint(path, 'first', lambda: {'ok': 1}), {'ok': 1})
            def should_not_run():
                self.fail('Completed checkpoint must be reused')
            self.assertEqual(worker.checkpoint(path, 'first', should_not_run), {'ok': 1})
            self.assertEqual(worker.checkpoint(path, 'changed', lambda: {'ok': 2}), {'ok': 2})
            path.write_text('broken', encoding='utf-8')
            self.assertEqual(worker.checkpoint(path, 'changed', lambda: {'ok': 3}), {'ok': 3})

    def test_json_nonfinite_values(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'result.json'
            worker.atomic_json(path, {'score': float('nan'), 'end': float('inf')})
            self.assertEqual(json.loads(path.read_text()), {'score': None, 'end': None})

    def test_speaker_splitting_preserves_words(self):
        result = {'segments': [{'start': 0, 'end': 2, 'text': 'Hello world.', 'words': [
            {'start': 0, 'end': 1, 'word': 'Hello', 'speaker': 'A'},
            {'start': 1, 'end': 2, 'word': 'world.', 'speaker': 'B'}]}]}
        segments = worker.normalized_segments(result)
        self.assertEqual([segment['speaker'] for segment in segments], ['Speaker 1', 'Speaker 2'])
        self.assertEqual(' '.join(segment['text'] for segment in segments), 'Hello world.')
        self.assertTrue(all(segment['speaker'] == 'Me' for segment in worker.normalized_segments(result, False)))
        result['segments'][0]['words'].pop()
        self.assertEqual(worker.normalized_segments(result)[0]['text'], 'Hello world.')

    def test_transcribe_passes_explicit_string_task_for_language_switch(self):
        # WhisperX may otherwise reuse tokenizer.task, a numeric token ID in
        # faster-whisper 1.2.1, when rebuilding its tokenizer for another language.
        tree = ast.parse(source.read_text(encoding='utf-8'))
        calls = [node for node in ast.walk(tree)
                 if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                 and node.func.attr == 'transcribe'
                 and isinstance(node.func.value, ast.Name) and node.func.value.id == 'model']
        self.assertTrue(calls, 'Expected the production model.transcribe call')
        for call in calls:
            keywords = {keyword.arg: keyword.value for keyword in call.keywords}
            task = keywords.get('task')
            self.assertIsInstance(task, ast.Constant, 'Pass a literal task, not tokenizer.task')
            self.assertEqual(task.value, 'transcribe')
            language = keywords.get('language')
            self.assertIsInstance(language, ast.Name)
            self.assertEqual(language.id, 'language')

    def test_danish_alignment_security_contract(self):
        tree = ast.parse(source.read_text(encoding='utf-8'))
        helper = next(node for node in tree.body if isinstance(node, ast.FunctionDef)
                      and node.name == 'load_danish_alignment')
        self.assertEqual(worker.DANISH_ALIGNMENT_REVISION, '7a60985d596e7a9e26b01a0707fe59daac6ffe84')
        assignments = [node for node in helper.body if isinstance(node, ast.Assign)]
        options = next(node.value for node in assignments
                       if any(isinstance(target, ast.Name) and target.id == 'options' for target in node.targets))
        options_kw = {keyword.arg: keyword.value for keyword in options.keywords}
        self.assertEqual(ast.unparse(options_kw['revision']), 'DANISH_ALIGNMENT_REVISION')
        self.assertIs(ast.literal_eval(options_kw['trust_remote_code']), False)
        self.assertEqual(ast.unparse(options_kw['local_files_only']), 'offline')
        calls = {node.func.value.id: node for node in ast.walk(helper)
                 if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                 and node.func.attr == 'from_pretrained' and isinstance(node.func.value, ast.Name)}
        self.assertEqual(set(calls), {'Wav2Vec2Config', 'Wav2Vec2Processor', 'Wav2Vec2ForCTC'})
        for call in calls.values():
            self.assertEqual(ast.unparse(call.args[0]), "ALIGN_MODELS['da']")
            self.assertTrue(any(keyword.arg is None and ast.unparse(keyword.value) == 'options'
                                for keyword in call.keywords))
        model = calls['Wav2Vec2ForCTC']
        model_kw = {keyword.arg: keyword.value for keyword in model.keywords}
        for name, expected in [('attn_implementation', 'eager'), ('use_kernels', False), ('weights_only', True)]:
            self.assertEqual(ast.literal_eval(model_kw[name]), expected)
        self.assertEqual(ast.unparse(model_kw['config']), 'config')
        eager = next(node for node in assignments
                     if any(ast.unparse(target) == 'config._attn_implementation' for target in node.targets))
        self.assertEqual(ast.literal_eval(eager.value), 'eager')
        self.assertLess(calls['Wav2Vec2Config'].lineno, eager.lineno)
        self.assertLess(eager.lineno, model.lineno)
        guard = next(node for node in helper.body if isinstance(node, ast.If)
                     and 'quantization_config' in ast.unparse(node.test))
        self.assertEqual(ast.unparse(guard.test), "getattr(config, 'quantization_config', None) is not None")
        self.assertTrue(any(isinstance(node, ast.Raise) for node in guard.body))
        self.assertLess(guard.lineno, calls['Wav2Vec2Processor'].lineno)
        self.assertLess(guard.lineno, model.lineno)

    def test_resident_same_language_reuses_all_models(self):
        cache = worker.ResidentModels()
        loaders = {role: Mock(side_effect=lambda: Mock()) for role in ('asr', 'align', 'diarization')}
        key = ('root', 'engine', 'base', 'cpu', 0, 'int8', 4)
        with patch.object(worker, 'emit') as emit:
            try:
                cache.select(key)
                cache.select_language('en')
                first = {role: cache.get(role, loader) for role, loader in loaders.items()}
                cache.select(key)
                cache.select_language('en')
                for role, loader in loaders.items():
                    self.assertIs(cache.get(role, loader), first[role])
                    loader.assert_called_once_with()
                self.assertEqual(emit.call_count, 3)
            finally:
                cache.close()

    def test_resident_language_switch_keeps_asr_and_diarizer(self):
        cache = worker.ResidentModels()
        class Model:
            pass
        key = ('root', 'engine', 'base', 'cpu', 0, 'int8', 4)
        asr_loader, diarizer_loader = Mock(side_effect=Model), Mock(side_effect=Model)
        with patch.object(worker, 'emit') as emit:
            try:
                cache.select(key)
                cache.select_language('en')
                asr = cache.get('asr', asr_loader)
                diarizer = cache.get('diarization', diarizer_loader)
                old_aligner = weakref.ref(cache.get('align', Model))
                cache.select(key)
                cache.select_language('da')
                self.assertIsNone(old_aligner(), 'Old language aligner must be released before replacement')
                self.assertIs(cache.get('asr', asr_loader), asr)
                self.assertIs(cache.get('diarization', diarizer_loader), diarizer)
                cache.get('align', Model)
                asr_loader.assert_called_once_with()
                diarizer_loader.assert_called_once_with()
                self.assertEqual([call.kwargs['role'] for call in emit.call_args_list],
                                 ['asr', 'diarization', 'align', 'align'])
                cache.select(('root', 'engine', 'medium', 'cpu', 0, 'int8', 4))
                self.assertEqual(cache.models, {})
                self.assertIsNone(cache.alignment_language)
            finally:
                cache.close()

    def test_resident_gpu_auxiliary_not_retained(self):
        cache = worker.ResidentModels()
        with patch.object(worker, 'emit'):
            try:
                asr = cache.get('asr', object)
                loader = Mock(side_effect=object)
                cache.get('align', loader, retain=False)
                cache.get('align', loader, retain=False)
                self.assertEqual(loader.call_count, 2)
                self.assertNotIn('align', cache.models)
                self.assertIs(cache.get('asr', object), asr)
            finally:
                cache.close()

    def test_diagnostics_durable_sanitized_and_append_across_runs(self):
        with tempfile.TemporaryDirectory() as directory:
            log = worker.WorkerDiagnostics(directory)
            secret = 'PRIVATE/audio.wav token=secret transcript words'
            request = {'mode': 'transcribe', 'language': 'da', 'audio': secret, 'token': secret,
                       'settings': {'model': 'base', 'device': 'cuda', 'gpuIndex': 1,
                                    'cpuThreads': 4, 'batchSize': 1, 'computeType': 'float16',
                                    'speakerCount': 0, 'diarization': True, 'extra': secret}}
            def fsync_after_flush(descriptor):
                self.assertTrue(log.path.read_bytes().endswith(b'\n'))
            with patch.object(worker.os, 'fsync', side_effect=fsync_after_flush) as sync:
                log.settings(request)
                log.error(RuntimeError(secret))
                worker.WorkerDiagnostics(directory).write('completed', stage='transcribe')
                self.assertEqual(sync.call_count, 3)
            text = log.path.read_text(encoding='utf-8')
            self.assertNotIn(secret, text)
            self.assertNotIn('token', text)
            records = [json.loads(line) for line in text.splitlines()]
            self.assertEqual(records[0]['gpuIndex'], 1)
            self.assertEqual(records[1]['errorClass'], 'RuntimeError')
            self.assertEqual(len(records), 3)
            for record in records:
                self.assertIsInstance(record['pid'], int)
                self.assertTrue(record['utc'].endswith('+00:00'))

    def test_diagnostics_rotation_is_bounded_and_preserves_prior_records(self):
        with tempfile.TemporaryDirectory() as directory:
            log = worker.WorkerDiagnostics(directory)
            log.MAX_BYTES = 350
            for _ in range(30):
                log.write('before', stage='load_asr')
            files = list(log.path.parent.glob('worker-diagnostics.jsonl*'))
            self.assertEqual(len(files), log.BACKUPS + 1)
            for file in files:
                self.assertLessEqual(file.stat().st_size, log.MAX_BYTES)
                self.assertTrue(file.read_text().strip())
            previous = log.path.read_bytes()
            worker.WorkerDiagnostics(directory).write('after', stage='load_asr')
            self.assertTrue(log.path.read_bytes().startswith(previous))

    def test_diagnostics_failures_never_abort_stage(self):
        with tempfile.TemporaryDirectory() as directory:
            log = worker.WorkerDiagnostics(directory)
            with patch.object(worker.os, 'fsync', side_effect=OSError('private detail')):
                with log.stage('infer_asr'):
                    pass
            # A library error still propagates unchanged, without its message in logs.
            with self.assertRaisesRegex(ValueError, 'private detail'):
                with log.stage('infer_align'):
                    raise ValueError('private detail')
            self.assertNotIn('private detail', log.path.read_text())
            with patch.object(Path, 'mkdir', side_effect=PermissionError('private detail')):
                log.write('before', stage='imports')

    def test_diagnostics_gpu_snapshot_uses_requested_index_only(self):
        with tempfile.TemporaryDirectory() as directory:
            log = worker.WorkerDiagnostics(directory)
            torch = Mock()
            props = Mock(name='gpu_props')
            props.name = 'NVIDIA Test GPU'
            props.total_memory = 8000
            torch.cuda.get_device_properties.return_value = props
            torch.cuda.mem_get_info.return_value = (6000, 8000)
            for name in ('memory_allocated', 'memory_reserved', 'max_memory_allocated', 'max_memory_reserved'):
                getattr(torch.cuda, name).return_value = 100
            log.memory(torch, {'device': 'cuda', 'gpuIndex': 1}, 'load_asr_before')
            for name in ('get_device_properties', 'mem_get_info', 'memory_allocated', 'memory_reserved',
                         'max_memory_allocated', 'max_memory_reserved'):
                getattr(torch.cuda, name).assert_called_once_with(1)
            torch.cuda.set_device.assert_not_called()
            torch.cuda.synchronize.assert_not_called()
            record = json.loads(log.path.read_text())
            self.assertEqual(record['gpuIndex'], 1)
            self.assertEqual(record['gpuName'], 'NVIDIA Test GPU')
            self.assertEqual(record['freeBytes'], 6000)
            torch.cuda.mem_get_info.side_effect = RuntimeError('private detail')
            log.memory(torch, {'device': 'cuda', 'gpuIndex': 1}, 'infer_asr_after')
            self.assertNotIn('private detail', log.path.read_text())

    def test_offline_guard(self):
        originals = socket.socket.connect, socket.socket.connect_ex, socket.create_connection
        try:
            with tempfile.TemporaryDirectory() as directory:
                worker.configure(Path(directory), True)
                with self.assertRaisesRegex(RuntimeError, 'Network is disabled'):
                    socket.create_connection(('127.0.0.1', 9))
                with socket.socket() as connection:
                    with self.assertRaisesRegex(RuntimeError, 'Network is disabled'):
                        connection.connect_ex(('127.0.0.1', 9))
        finally:
            socket.socket.connect, socket.socket.connect_ex, socket.create_connection = originals


if __name__ == '__main__':
    unittest.main()
