"""Dependency-free worker checks: no models, network, or real meeting audio."""
import importlib.util
import json
from pathlib import Path
import socket
import tempfile
import unittest

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
