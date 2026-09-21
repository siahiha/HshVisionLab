"""Create an experimental CPU-compatible QDQ INT8 ONNX model.

For a production-quality model, replace SyntheticCalibrationReader with real
preprocessed camera frames representing daytime, night, glare and motion.
"""

from pathlib import Path
import sys

import numpy as np
import onnx
from onnxruntime.quantization import (
    CalibrationDataReader,
    CalibrationMethod,
    QuantFormat,
    QuantType,
    quantize_static,
)


class SyntheticCalibrationReader(CalibrationDataReader):
    def __init__(self, input_name: str, count: int = 32) -> None:
        rng = np.random.default_rng(42)
        self._batches = iter(
            {
                input_name: rng.uniform(0.0, 1.0, (1, 3, 416, 416)).astype(np.float32)
            }
            for _ in range(count)
        )

    def get_next(self):
        return next(self._batches, None)


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    source_name = sys.argv[1] if len(sys.argv) > 1 else "best_416_static.onnx"
    destination_name = sys.argv[2] if len(sys.argv) > 2 else "best_416_int8_qdq_experimental.onnx"
    source = root / "Models" / source_name
    destination = root / "Models" / destination_name

    if not source.is_file():
        raise FileNotFoundError(source)

    model = onnx.load(source, load_external_data=False)
    input_name = model.graph.input[0].name
    quantize_static(
        model_input=str(source),
        model_output=str(destination),
        calibration_data_reader=SyntheticCalibrationReader(input_name),
        quant_format=QuantFormat.QDQ,
        activation_type=QuantType.QUInt8,
        weight_type=QuantType.QInt8,
        per_channel=True,
        reduce_range=False,
        calibrate_method=CalibrationMethod.MinMax,
        op_types_to_quantize=["Conv", "MatMul"],
    )
    print(destination)


if __name__ == "__main__":
    sys.exit(main())
