from app import geometry


def test_gemini_box_is_converted_to_pixels():
    # Gemini: [ymin, xmin, ymax, xmax] on 0-1000
    assert geometry.gemini_to_pixels([100, 200, 500, 800], 1000, 500) == (200, 50, 800, 250)


def test_invalid_gemini_box_gives_none():
    assert geometry.gemini_to_pixels(None, 100, 100) is None
    assert geometry.gemini_to_pixels([1, 2, 3], 100, 100) is None


def test_yolo_round_trip():
    box = (100, 50, 300, 250)
    yolo = geometry.pixels_to_yolo(box, 400, 300)
    assert yolo == (0.5, 0.5, 0.5, 0.666667)
    assert geometry.yolo_to_pixels(*yolo, 400, 300) == box


def test_scale_box_to_original_size():
    assert geometry.scale_box((10, 20, 30, 40), (100, 100), (200, 300)) == (20, 60, 60, 120)
