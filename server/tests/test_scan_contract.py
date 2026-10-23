import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import cv2
import numpy as np
import trimesh
from scipy.spatial.transform import Rotation
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main
from frame_handler import FrameHandler
from protocol import ProtocolError, parse_frame
from reconstruction import ReconstructionError, ReconstructionPipeline, evaluate_metric_alignment, make_match_pairs, make_roi_masks


def make_frame(index=0, timestamp="2026-10-23T00:00:00Z", fx=90.0):
    image = np.tile(np.arange(64, dtype=np.uint8), (48, 1))
    encoded = cv2.imencode(".jpg", image)[1].tobytes()
    meta = {
        "protocol_version": 2, "frame_index": index,
        "camera_timestamp": timestamp, "camera_id": "left",
        "image_orientation": "top_left",
        "pose_space": "unity_world_camera_to_world",
        "image_width": 64, "image_height": 48,
        "intrinsics": {"fx": fx, "fy": 90.0, "cx": 32.0, "cy": 24.0},
        "pose": {"position": {"x": 1.0, "y": 2.0, "z": 3.0},
                 "rotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}},
    }
    metadata = json.dumps(meta).encode()
    return struct.pack("<4sIII", b"MSF2", len(metadata), len(encoded), 0) + metadata + encoded, encoded


class ProtocolTests(unittest.TestCase):
    def test_strict_lengths_and_physical_camera(self):
        packet, jpeg = make_frame()
        meta, actual_jpeg, depth = parse_frame(packet)
        self.assertEqual(actual_jpeg, jpeg)
        self.assertEqual(meta["camera_id"], "left")
        self.assertEqual(depth, b"")
        for altered in (packet[:-1], packet + b"x", b"MSF\x01" + packet[4:]):
            with self.subTest(length=len(altered)):
                with self.assertRaises(ProtocolError):
                    parse_frame(altered)

    def test_original_jpeg_duplicate_and_calibration(self):
        with tempfile.TemporaryDirectory() as folder:
            handler = FrameHandler("test_session", Path(folder))
            packet, jpeg = make_frame()
            self.assertEqual(handler.process_frame(packet)["status"], "accepted")
            self.assertEqual((handler.images_dir / "frame_000000.jpg").read_bytes(), jpeg)
            self.assertEqual(handler.process_frame(packet)["reason"], "duplicate_frame")
            changed, _ = make_frame(1, "next", 101.0)
            self.assertEqual(handler.process_frame(changed)["reason"], "camera_calibration_changed")
            handler.prepare_colmap_input()
            saved = handler.save_session_metadata()
            self.assertEqual(saved["total_frames"], 1)
            self.assertEqual(saved["rejected_frames"], 2)
            self.assertEqual(saved["frames"][0]["quality_ok"], False)
            self.assertEqual(handler.process_frame(changed)["reason"], "session_finalized")

    def test_captured_image_integrity_prevents_stale_reconstruction(self):
        with tempfile.TemporaryDirectory() as folder:
            handler = FrameHandler("checksum", Path(folder))
            handler.process_frame(make_frame()[0])
            handler.prepare_colmap_input()
            handler.save_session_metadata()
            pipeline = ReconstructionPipeline(handler.session_dir, "checksum")
            with patch("reconstruction.MIN_FRAMES_FOR_RECONSTRUCTION", 1):
                pipeline._load_session()
                (handler.session_dir / "colmap" / "images" / "frame_000000.jpg").write_bytes(b"bad")
                with self.assertRaisesRegex(ReconstructionError, "changed"):
                    pipeline._load_session()

    def test_missing_cached_artifact_rebuilds_downstream_stages(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            pipeline = ReconstructionPipeline(root, "cache")
            pipeline.run_dir = root / "run"
            pipeline.run_dir.mkdir()
            feature_ok = pipeline.run_dir / "features.ok"
            feature_ok.write_text("ok")
            (pipeline.run_dir / "matching.ok").write_text("ok")
            artifact = pipeline.run_dir / "feature.db"
            pipeline._step("features", [sys.executable, "-c",
                        "import pathlib,sys; pathlib.Path(sys.argv[1]).write_text('new')",
                        str(artifact)], 10, feature_ok, (artifact,))
            self.assertTrue(feature_ok.exists())
            self.assertEqual(artifact.read_text(), "new")
            self.assertFalse((pipeline.run_dir / "matching.ok").exists())

    def test_metric_alignment_requires_coverage_and_pose_agreement(self):
        with tempfile.TemporaryDirectory() as folder:
            text_path = Path(folder) / "images.txt"
            frames = []
            lines = ["# COLMAP images\n"]
            for i in range(20):
                name = f"frame_{i:06d}.jpg"
                x = i * 0.02
                frames.append({"image": name, "pose": {"position": {"x": x, "y": 0, "z": 0}}})
                lines.append(f"{i + 1} 1 0 0 0 {-x} 0 0 1 {name}\n\n")
            text_path.write_text("".join(lines))
            report = evaluate_metric_alignment(text_path, frames)
            self.assertEqual(report["registered_images"], 20)
            self.assertLess(report["median_camera_error_m"], 1e-7)
            text_path.write_text("".join(lines[:11]))
            with self.assertRaisesRegex(ReconstructionError, "registered"):
                evaluate_metric_alignment(text_path, frames)

    def test_pose_conversion_and_loop_pairs(self):
        pose = {"position": {"x": 1, "y": 2, "z": 3},
                "rotation": {"x": 0, "y": 0, "z": 0, "w": 1}}
        qw, qx, qy, qz, tx, ty, tz = FrameHandler._unity_pose_to_colmap(pose)
        self.assertAlmostEqual(abs(qx), 1.0)
        np.testing.assert_allclose([qw, qy, qz, tx, ty, tz], [0, 0, 0, -1, 2, -3], atol=1e-8)
        frames = [
            {"image": f"frame_{i:03}.jpg",
             "pose": {"position": {"x": float(i % 5) * 0.03, "y": 0, "z": 0}}}
            for i in range(20)
        ]
        pairs = make_match_pairs(frames, neighbors=2, loops=2)
        self.assertTrue(any(a == "frame_000.jpg" and int(b[6:9]) > 2 for a, b in pairs))
        self.assertLess(len(pairs), 100)

    def test_pose_conversion_preserves_ray_geometry(self):
        point_in_unity_camera = np.array([0.12, -0.05, 0.8])
        world_flip = np.diag([1, 1, -1])
        camera_flip = np.diag([1, -1, 1])
        for angle in (0, 15, 50, 110):
            unity_rotation = Rotation.from_euler("xyz", [angle, -angle / 3, angle / 2], degrees=True)
            x, y, z, w = unity_rotation.as_quat()
            position = np.array([0.7, 1.4, -0.3])
            pose = {"position": dict(zip(("x", "y", "z"), position)),
                    "rotation": {"x": x, "y": y, "z": z, "w": w}}
            qw, qx, qy, qz, tx, ty, tz = FrameHandler._unity_pose_to_colmap(pose)
            world_point = world_flip @ (unity_rotation.apply(point_in_unity_camera) + position)
            projected = Rotation.from_quat([qx, qy, qz, qw]).apply(world_point) + [tx, ty, tz]
            np.testing.assert_allclose(projected, camera_flip @ point_in_unity_camera, atol=1e-8)

    def test_metric_mesh_and_millimetre_stl(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            session = root / "session"
            output = root / "exports"
            pipeline = ReconstructionPipeline(session, "session", output)
            pipeline.run_dir = session / "colmap" / "runs" / "sample"
            source = pipeline.run_dir / "dense" / "meshed-poisson.ply"
            source.parent.mkdir(parents=True)
            cube = trimesh.creation.box(extents=[0.2, 0.3, 0.4])
            for _ in range(2):
                cube = cube.subdivide()
            cube.apply_translation([1, 2, -3])
            cube.export(source)
            meta = {"roi": {"center": {"x": 1, "y": 2, "z": 3}, "radius": 1}}
            exports, validation = pipeline._export_metric_mesh(meta)
            self.assertTrue(validation["watertight"])
            self.assertEqual(len(exports), 5)
            print_mesh = trimesh.load(output / "session_print_candidate_mm.stl")
            np.testing.assert_allclose(print_mesh.extents, [200, 400, 300], atol=1e-3)
            np.testing.assert_allclose(print_mesh.bounds[0, 2], 0, atol=1e-6)
            np.testing.assert_allclose(print_mesh.bounds.mean(axis=0)[:2], [0, 0], atol=1e-6)
            editable = trimesh.load(output / "session_object_editable.glb", force="mesh")
            np.testing.assert_allclose(editable.extents, [0.2, 0.3, 0.4], atol=1e-6)

    def test_roi_projection_masks_dense_fusion(self):
        packet, _ = make_frame()
        meta, _, _ = parse_frame(packet)
        capture = {
            "roi": {"center": {"x": 1, "y": 2, "z": 4}, "radius": 0.1},
            "intrinsics": {**meta["intrinsics"], "width": 64, "height": 48},
            "frames": [{"image": "frame_000000.jpg", "pose": meta["pose"]}],
        }
        with tempfile.TemporaryDirectory() as folder:
            masks = make_roi_masks(capture, Path(folder))
            image = cv2.imread(str(masks / "frame_000000.jpg.png"), cv2.IMREAD_GRAYSCALE)
            self.assertEqual(image[24, 32], 255)
            self.assertEqual(image[0, 0], 0)

    def test_websocket_stop_is_durable(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            original = FrameHandler
            with patch.object(main, "SESSIONS_DIR", root), patch.object(
                main, "FrameHandler", side_effect=lambda session_id, roi=None: original(session_id, root, roi)
            ):
                with TestClient(main.app) as client:
                    with client.websocket_connect("/ws/scan") as socket:
                        socket.send_json({"action": "start_session", "name": "sample"})
                        started = socket.receive_json()
                        session_id = started["session_id"]
                        packet, jpeg = make_frame()
                        socket.send_bytes(packet)
                        feedback = socket.receive_json()
                        self.assertEqual(feedback["status"], "accepted")
                        socket.send_json({"action": "stop_session"})
                        stopped = socket.receive_json()
                        self.assertEqual(stopped["total_frames"], 1)
                        self.assertFalse(stopped["ready_for_reconstruction"])
                        socket.send_json({"action": "start_session", "name": "second"})
                        second_id = socket.receive_json()["session_id"]
                        self.assertNotEqual(second_id, session_id)
                        socket.send_bytes(make_frame()[0])
                        self.assertEqual(socket.receive_json()["status"], "accepted")
                        socket.send_json({"action": "stop_session"})
                        self.assertEqual(socket.receive_json()["total_frames"], 1)
                    meta = json.loads((root / session_id / "session_metadata.json").read_text())
                    self.assertEqual(meta["status"], "captured")
                    self.assertEqual((root / session_id / "colmap" / "images" / "frame_000000.jpg").read_bytes(), jpeg)
                    self.assertEqual(json.loads((root / second_id / "session_metadata.json").read_text())["status"], "captured")

    def test_custom_export_manifest_is_downloadable_and_failure_hides_stale_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / "sessions"
            session = root / "job"
            session.mkdir(parents=True)
            custom = Path(folder) / "exports" / "job" / "custom"
            custom.mkdir(parents=True)
            artifact = custom / "job_object_editable.obj"
            artifact.write_bytes(b"v 0 0 0\n")
            item = {"filename": artifact.name, "path": str(artifact),
                    "url": f"/api/sessions/job/exports/{artifact.name}"}
            state = {"status": "completed", "result": {"exports": [item]}}
            (session / "reconstruction_job.json").write_text(json.dumps(state))
            with patch.object(main, "SESSIONS_DIR", root):
                with TestClient(main.app) as client:
                    listing = client.get("/api/sessions/job/exports").json()
                    self.assertEqual(listing["exports"], [item])
                    self.assertEqual(client.get(item["url"]).content, artifact.read_bytes())
                    self.assertEqual(client.get("/api/sessions/job/exports/other.obj").status_code, 404)
                    state["status"] = "failed"
                    (session / "reconstruction_job.json").write_text(json.dumps(state))
                    self.assertEqual(client.get("/api/sessions/job/exports").json()["exports"], [])
                    self.assertEqual(client.get(item["url"]).status_code, 404)

    def test_disconnect_is_interrupted_and_cannot_reconstruct(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            original = FrameHandler
            with patch.object(main, "SESSIONS_DIR", root), patch.object(
                main, "FrameHandler", side_effect=lambda session_id, roi=None: original(session_id, root, roi)
            ):
                with TestClient(main.app) as client:
                    with client.websocket_connect("/ws/scan") as socket:
                        socket.send_json({"action": "start_session", "name": "broken"})
                        session_id = socket.receive_json()["session_id"]
                        socket.send_bytes(make_frame()[0])
                        self.assertEqual(socket.receive_json()["status"], "accepted")
                    status = client.post(f"/api/sessions/{session_id}/reconstruct")
                    self.assertEqual(status.status_code, 409)
                    meta = json.loads((root / session_id / "session_metadata.json").read_text())
                    self.assertEqual(meta["status"], "interrupted")

    def test_websocket_reconstruction_starts_and_persists_status(self):
        class FakePipeline:
            def __init__(self, session_dir, session_id, output_dir):
                self.session_id = session_id

            def run_colmap_pipeline(self, quality, progress_cb):
                progress_cb(55, "synthetic stage")
                return {"session_id": self.session_id, "status": "completed", "exports": []}

        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            original = FrameHandler
            with patch.object(main, "SESSIONS_DIR", root), patch.object(
                main, "FrameHandler", side_effect=lambda session_id, roi=None: original(session_id, root, roi)
            ), patch.object(main, "ReconstructionPipeline", FakePipeline):
                with TestClient(main.app) as client:
                    with client.websocket_connect("/ws/scan") as socket:
                        socket.send_json({"action": "start_session", "name": "job"})
                        session_id = socket.receive_json()["session_id"]
                        for index in range(20):
                            socket.send_bytes(make_frame(index, str(index))[0])
                            self.assertEqual(socket.receive_json()["status"], "accepted")
                        socket.send_json({"action": "stop_session"})
                        self.assertTrue(socket.receive_json()["ready_for_reconstruction"])
                        self.assertEqual(client.post(
                            f"/api/sessions/{session_id}/reconstruct?output_dir=..%2Foutside"
                        ).status_code, 400)
                        socket.send_json({"action": "start_reconstruction", "method": "colmap"})
                        self.assertEqual(socket.receive_json()["action"], "reconstruction_started")
                        for _ in range(20):
                            socket.send_json({"action": "get_reconstruction_status"})
                            status = socket.receive_json()
                            if status["status"] == "completed":
                                break
                        self.assertEqual(status["status"], "completed")
                    main.reconstruction_tasks.clear()
                    self.assertEqual(main._job_status(session_id)["status"], "completed")


if __name__ == "__main__":
    unittest.main()
