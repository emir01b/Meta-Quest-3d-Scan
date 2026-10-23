"""Run COLMAP model_aligner on a known scale and translation."""

import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from config import COLMAP_BINARY


def run(args):
    command = [COLMAP_BINARY, *args]
    result = subprocess.run(command, capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=30)
    if result.returncode:
        raise RuntimeError(f"{args[0]} failed: {result.stderr[-1500:]}")
    return result.stdout + result.stderr


def main():
    positions = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0],
                          [0, 0, 1], [1, 1, 0], [0, 1, 1]], dtype=float)
    scale = 0.27
    shift = np.array([1.0, -2.0, 3.0])
    with tempfile.TemporaryDirectory(prefix="metascan_align_") as folder:
        root = Path(folder)
        source, target, text_dir = (root / item for item in ("source", "aligned", "text"))
        for path in (source, target, text_dir):
            path.mkdir()
        (source / "cameras.txt").write_text("1 PINHOLE 500 400 350 350 250 200\n")
        lines = []
        refs = []
        for i, point in enumerate(positions, 1):
            name = f"frame_{i:06}.jpg"
            lines.append(f"{i} 1 0 0 0 {-point[0]} {-point[1]} {-point[2]} 1 {name}\n\n")
            physical = point * scale + shift
            refs.append(f"{name} {physical[0]} {physical[1]} {physical[2]}\n")
        (source / "images.txt").write_text("".join(lines))
        (source / "points3D.txt").write_text("# empty\n")
        reference = root / "reference.txt"
        reference.write_text("".join(refs))
        log = run(["model_aligner", "--input_path", str(source),
                   "--output_path", str(target), "--ref_images_path", str(reference),
                   "--ref_is_gps", "0", "--alignment_type", "custom",
                   "--alignment_max_error", "0.02", "--min_common_images", "5"])
        run(["model_converter", "--input_path", str(target),
             "--output_path", str(text_dir), "--output_type", "TXT"])
        aligned_lines = [line for line in (text_dir / "images.txt").read_text().splitlines()
                         if line and not line.startswith("#")]
        image_records = [line for line in aligned_lines if ".jpg" in line]
        assert len(image_records) == 6, image_records
        for line in image_records:
            fields = line.split()
            index = int(fields[0]) - 1
            quaternion = np.array(list(map(float, fields[1:5])))
            translation = np.array(list(map(float, fields[5:8])))
            # The generated model uses identity camera rotations; alignment here
            # estimates only scale and translation.
            assert abs(quaternion[0]) > 0.999
            np.testing.assert_allclose(-translation, positions[index] * scale + shift, atol=1e-4)
        print("COLMAP metric alignment ok, 6 cameras, scale", scale)
        print(log[-500:])


if __name__ == "__main__":
    main()
