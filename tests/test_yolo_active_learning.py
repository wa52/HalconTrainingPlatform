"""Stdlib-only smoke tests for COCO -> YOLO conversion.

Run: python -m unittest discover -s tests -p 'test_yolo_active_learning.py'
No torch, HALCON, images or internet required.
"""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

WORKER = Path(__file__).resolve().parents[1] / "tools" / "yolo_active_learning.py"
spec = importlib.util.spec_from_file_location("yolo_active_learning", WORKER)
assert spec is not None and spec.loader is not None
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class YoloSeedTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.images = self.root / "images"
        self.images.mkdir()
        for n in range(1, 6):
            (self.images / f"{n:02}.png").write_bytes(b"placeholder")
        self.data = {
            "images": [
                {"id": n, "file_name": f"{n:02}.png", "width": 100, "height": 80}
                for n in range(1, 6)
            ],
            "categories": [{"id": 1, "name": "scratch"}, {"id": 8, "name": "dent"}],
            "annotations": [
                {"id": 1, "image_id": 1, "category_id": 1, "bbox": [10, 15, 20, 10], "iscrowd": 0},
                {"id": 2, "image_id": 2, "category_id": 8, "bbox": [4, 5, 40, 30], "iscrowd": 0},
            ],
        }
        self.coco = self.root / "annotations.json"

    def tearDown(self):
        self.temp.cleanup()

    def write(self):
        self.coco.write_text(json.dumps(self.data), encoding="utf-8")

    def test_five_approved_images_enough_and_negatives_preserved(self):
        self.write()
        info = module.prepare_coco(str(self.coco), str(self.root / "yolo"))
        self.assertEqual(info["approvedImages"], 5)
        self.assertEqual(info["approvedBoxes"], 2)
        self.assertEqual(info["classIds"], [1, 8])
        self.assertEqual(len(list((self.root / "yolo" / "images" / "train").glob("*"))), 5)
        self.assertEqual((self.root / "yolo" / "labels" / "train" / "03.txt").read_text(), "")
        labels = (self.root / "yolo" / "labels" / "train" / "01.txt").read_text().strip().split()
        self.assertEqual(labels[0], "0")  # COCO class 1 -> contiguous YOLO class 0.
        self.assertAlmostEqual(float(labels[1]), .20)
        self.assertAlmostEqual(float(labels[2]), .25)
        self.assertAlmostEqual(float(labels[3]), .20)
        self.assertAlmostEqual(float(labels[4]), .125)
        self.assertEqual((self.root / "yolo" / "labels" / "train" / "02.txt").read_text().split()[0], "1")
        config = json.loads((self.root / "yolo" / "data.yaml").read_text(encoding="utf-8"))
        self.assertEqual(config["nc"], 2)

    def test_reject_four_images(self):
        self.data["images"].pop()
        self.write()
        with self.assertRaisesRegex(ValueError, "5"):
            module.prepare_coco(str(self.coco), str(self.root / "yolo"))

    def test_reject_missing_category_positive_boxes(self):
        self.data["annotations"].pop()
        self.write()
        with self.assertRaisesRegex(ValueError, "dent"):
            module.prepare_coco(str(self.coco), str(self.root / "yolo"))

    def test_reject_out_of_bounds_boxes(self):
        self.data["annotations"][0]["bbox"] = [90, 60, 30, 50]
        self.write()
        with self.assertRaisesRegex(ValueError, "越界"):
            module.prepare_coco(str(self.coco), str(self.root / "yolo"))


if __name__ == "__main__":
    unittest.main()
