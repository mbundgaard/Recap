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
