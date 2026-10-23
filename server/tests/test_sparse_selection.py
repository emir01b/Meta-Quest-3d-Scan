import struct
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from reconstruction import select_sparse_model, ReconstructionError


class SparseSelectionTests(unittest.TestCase):
    def test_largest_registered_model_not_first(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for name, count in [('0', 2), ('1', 209)]:
                model = root / name
                model.mkdir()
                (model / 'images.bin').write_bytes(struct.pack('<Q', count))
                (model / 'points3D.bin').write_bytes(struct.pack('<Q', 0))
            self.assertEqual(select_sparse_model(root).name, '1')

    def test_missing_model_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(ReconstructionError):
                select_sparse_model(Path(tmp))
