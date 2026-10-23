"""Optional real COLMAP GPU feature/matcher smoke check: run this file directly."""

import sqlite3
import subprocess
import sys
import tempfile
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from config import COLMAP_BINARY


def main():
    colmap = Path(COLMAP_BINARY)
    if not colmap.is_file():
        raise SystemExit(f"COLMAP missing: {colmap}")
    with tempfile.TemporaryDirectory(prefix="metascan_colmap_") as folder:
        root = Path(folder)
        images = root / "images"
        images.mkdir()
        rng = np.random.default_rng(33)
        image = rng.integers(20, 235, size=(400, 500, 3), dtype=np.uint8)
        for i in range(3):
            transform = np.float32([[1, 0, i * 12], [0, 1, i * 5]])
            shifted = cv2.warpAffine(image, transform, (500, 400))
            cv2.imwrite(str(images / f"frame_{i:06}.jpg"), shifted)
        db = root / "database.db"
        pairs = root / "pairs.txt"
        pairs.write_text("frame_000000.jpg frame_000001.jpg\n"
                         "frame_000001.jpg frame_000002.jpg\n")
        steps = [
            [str(colmap), "feature_extractor", "--database_path", str(db),
             "--image_path", str(images), "--ImageReader.single_camera", "1",
             "--ImageReader.camera_model", "PINHOLE",
             "--ImageReader.camera_params", "350,350,250,200",
             "--FeatureExtraction.use_gpu", "1",
             "--SiftExtraction.domain_size_pooling", "0"],
            [str(colmap), "matches_importer", "--database_path", str(db),
             "--match_list_path", str(pairs), "--match_type", "pairs",
             "--FeatureMatching.use_gpu", "1"],
        ]
        for command in steps:
            run = subprocess.run(command, capture_output=True, text=True,
                                 encoding="utf-8", errors="replace", timeout=90)
            if run.returncode:
                raise RuntimeError(f"{command[1]} failed: {run.stderr[-1200:]}")
            print(command[1], "ok")
        connection = sqlite3.connect(db)
        try:
            features = connection.execute("SELECT COUNT(*) FROM descriptors").fetchone()[0]
            matches = connection.execute("SELECT COUNT(*) FROM two_view_geometries WHERE rows > 0").fetchone()[0]
        finally:
            connection.close()
        print("feature images:", features, "verified pairs:", matches)
        if features != 3 or matches < 2:
            raise RuntimeError("COLMAP did not verify expected image pairs")


if __name__ == "__main__":
    main()
