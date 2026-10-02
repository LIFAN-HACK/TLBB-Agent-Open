using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

internal sealed record YoloDetection(
    int ClassId,
    string Label,
    VisionRectF Box,
    float Confidence);

internal sealed record YoloInferenceResult(
    IReadOnlyList<YoloDetection> Detections,
    IReadOnlyList<float> MaxClassScores,
    double InferenceMilliseconds,
    string ModelId);

internal sealed record YoloDecodeResult(
    IReadOnlyList<YoloDetection> Detections,
    IReadOnlyList<float> MaxClassScores);

internal sealed class YoloV8OnnxDetector : IDisposable
{
    private const float PaddingValue = 114f / 255f;

    private readonly object _gate = new();
    private readonly InferenceSession _session;
    private readonly DenseTensor<float> _inputTensor;
    private readonly NamedOnnxValue _inputValue;
    private readonly float[] _inputBuffer;
    private readonly IReadOnlyList<string> _classes;
    private readonly IReadOnlyDictionary<int, float> _thresholds;
    private readonly int _inputSize;
    private readonly float _nmsIou;
    private readonly int _maximumDetections;
    private readonly string _inputName;
    private readonly string _outputName;

    public YoloV8OnnxDetector(
        string modelPath,
        IReadOnlyList<string> classes,
        IReadOnlyDictionary<int, float> thresholds,
        int inputSize,
        float nmsIou,
        int maximumDetections,
        string expectedSha256 = "")
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("ONNX model not found.", modelPath);
        if (classes.Count == 0)
            throw new InvalidDataException("YOLO class list is empty.");

        var actualHash = ComputeSha256(modelPath);
        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"ONNX hash mismatch. expected={expectedSha256}, actual={actualHash}");
        }

        _classes = classes;
        _thresholds = thresholds;
        _inputSize = inputSize;
        _nmsIou = nmsIou;
        _maximumDetections = maximumDetections;
        Sha256 = actualHash;
        ModelId = actualHash[..12].ToLowerInvariant();

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = ResolveIntraOpThreads()
        };

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.Single();
        _outputName = _session.OutputMetadata.Keys.Single();

        ValidateModelContract();

        _inputBuffer = new float[3 * inputSize * inputSize];
        _inputTensor = new DenseTensor<float>(_inputBuffer, new[] { 1, 3, inputSize, inputSize });
        _inputValue = NamedOnnxValue.CreateFromTensor(_inputName, _inputTensor);
    }

    public string ModelId { get; }
    public string Sha256 { get; }
    public string InputName => _inputName;
    public string OutputName => _outputName;

    public YoloInferenceResult Detect(CaptureResult image)
    {
        ValidateImage(image);

        lock (_gate)
        {
            var transform = Preprocess(image);
            var stopwatch = Stopwatch.StartNew();
            using var output = _session.Run(new[] { _inputValue }, new[] { _outputName });
            stopwatch.Stop();

            var tensor = output.Single().AsTensor<float>();
            var decoded = Decode(tensor, transform, image.Width, image.Height);
            return new YoloInferenceResult(
                decoded.Detections,
                decoded.MaxClassScores,
                stopwatch.Elapsed.TotalMilliseconds,
                ModelId);
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    private void ValidateModelContract()
    {
        var input = _session.InputMetadata[_inputName];
        if (input.ElementType != typeof(float))
            throw new InvalidDataException($"Unsupported ONNX input type: {input.ElementType}.");

        var dimensions = input.Dimensions;
        if (dimensions.Length != 4 || (dimensions[1] > 0 && dimensions[1] != 3))
            throw new InvalidDataException($"Unexpected ONNX input shape: [{string.Join(',', dimensions)}].");

        var output = _session.OutputMetadata[_outputName];
        if (output.ElementType != typeof(float) || output.Dimensions.Length != 3)
            throw new InvalidDataException("YOLO output must be a rank-3 float tensor.");
    }

    private LetterboxTransform Preprocess(CaptureResult image)
    {
        Array.Fill(_inputBuffer, PaddingValue);

        var ratio = Math.Min((float)_inputSize / image.Width, (float)_inputSize / image.Height);
        var resizedWidth = Math.Max(1, (int)Math.Round(image.Width * ratio));
        var resizedHeight = Math.Max(1, (int)Math.Round(image.Height * ratio));
        var padX = (_inputSize - resizedWidth) / 2;
        var padY = (_inputSize - resizedHeight) / 2;
        var plane = _inputSize * _inputSize;

        for (var dy = 0; dy < resizedHeight; dy++)
        {
            var sy = Math.Clamp((dy + 0.5f) / ratio - 0.5f, 0, image.Height - 1);
            var y0 = (int)MathF.Floor(sy);
            var y1 = Math.Min(y0 + 1, image.Height - 1);
            var wy = sy - y0;

            for (var dx = 0; dx < resizedWidth; dx++)
            {
                var sx = Math.Clamp((dx + 0.5f) / ratio - 0.5f, 0, image.Width - 1);
                var x0 = (int)MathF.Floor(sx);
                var x1 = Math.Min(x0 + 1, image.Width - 1);
                var wx = sx - x0;

                var outputIndex = (padY + dy) * _inputSize + padX + dx;
                _inputBuffer[outputIndex] = BilinearChannel(image, x0, y0, x1, y1, wx, wy, 2) / 255f;
                _inputBuffer[plane + outputIndex] = BilinearChannel(image, x0, y0, x1, y1, wx, wy, 1) / 255f;
                _inputBuffer[2 * plane + outputIndex] = BilinearChannel(image, x0, y0, x1, y1, wx, wy, 0) / 255f;
            }
        }

        return new LetterboxTransform(ratio, padX, padY);
    }

    private YoloDecodeResult Decode(
        Tensor<float> tensor,
        LetterboxTransform transform,
        int sourceWidth,
        int sourceHeight)
    {
        var dimensions = tensor.Dimensions.ToArray();
        if (dimensions.Length != 3 || dimensions[0] != 1)
            throw new InvalidDataException($"Unexpected YOLO output shape: [{string.Join(',', dimensions)}].");

        var expectedChannels = 4 + _classes.Count;
        var channelsFirst = dimensions[1] == expectedChannels;
        var anchorsFirst = dimensions[2] == expectedChannels;
        if (!channelsFirst && !anchorsFirst)
        {
            throw new InvalidDataException(
                $"YOLO output does not contain {expectedChannels} channels: [{string.Join(',', dimensions)}].");
        }

        var anchors = channelsFirst ? dimensions[2] : dimensions[1];
        var candidates = new List<YoloDetection>(Math.Min(anchors, 256));
        var maxClassScores = new float[_classes.Count];

        for (var anchor = 0; anchor < anchors; anchor++)
        {
            var cx = Read(tensor, channelsFirst, 0, anchor);
            var cy = Read(tensor, channelsFirst, 1, anchor);
            var width = Read(tensor, channelsFirst, 2, anchor);
            var height = Read(tensor, channelsFirst, 3, anchor);
            if (width <= 0 || height <= 0)
                continue;

            for (var classId = 0; classId < _classes.Count; classId++)
            {
                var score = Read(tensor, channelsFirst, 4 + classId, anchor);
                maxClassScores[classId] = Math.Max(maxClassScores[classId], score);
                var threshold = _thresholds.TryGetValue(classId, out var configured) ? configured : 0.35f;
                if (score < threshold)
                    continue;

                var x1 = (cx - width / 2 - transform.PadX) / transform.Ratio;
                var y1 = (cy - height / 2 - transform.PadY) / transform.Ratio;
                var x2 = (cx + width / 2 - transform.PadX) / transform.Ratio;
                var y2 = (cy + height / 2 - transform.PadY) / transform.Ratio;

                x1 = Math.Clamp(x1, 0, sourceWidth);
                y1 = Math.Clamp(y1, 0, sourceHeight);
                x2 = Math.Clamp(x2, 0, sourceWidth);
                y2 = Math.Clamp(y2, 0, sourceHeight);
                if (x2 - x1 < 1 || y2 - y1 < 1)
                    continue;

                candidates.Add(new YoloDetection(
                    classId,
                    _classes[classId],
                    new VisionRectF(x1, y1, x2 - x1, y2 - y1),
                    score));
            }
        }

        var selected = new List<YoloDetection>(_maximumDetections * _classes.Count);
        foreach (var group in candidates.GroupBy(item => item.ClassId))
        {
            var selectedForClass = new List<YoloDetection>(_maximumDetections);
            foreach (var candidate in group.OrderByDescending(item => item.Confidence))
            {
                if (selectedForClass.Count >= _maximumDetections)
                    break;
                if (selectedForClass.Any(item => IntersectionOverUnion(item.Box, candidate.Box) > _nmsIou))
                    continue;

                selectedForClass.Add(candidate);
            }

            selected.AddRange(selectedForClass);
        }

        var bestPerClass = selected
            .GroupBy(item => item.ClassId)
            .Select(group => group.OrderByDescending(item => item.Confidence).First())
            .ToList();
        var remaining = selected
            .Where(item => !bestPerClass.Contains(item))
            .OrderByDescending(item => item.Confidence);

        var finalDetections = bestPerClass
            .Concat(remaining)
            .Take(_maximumDetections)
            .OrderByDescending(item => item.Confidence)
            .ToArray();
        return new YoloDecodeResult(finalDetections, maxClassScores);
    }

    private static float Read(Tensor<float> tensor, bool channelsFirst, int channel, int anchor) =>
        channelsFirst ? tensor[0, channel, anchor] : tensor[0, anchor, channel];

    private static float BilinearChannel(
        CaptureResult image,
        int x0,
        int y0,
        int x1,
        int y1,
        float wx,
        float wy,
        int channel)
    {
        var stride = image.Width * 4;
        var p00 = image.Pixels[y0 * stride + x0 * 4 + channel];
        var p10 = image.Pixels[y0 * stride + x1 * 4 + channel];
        var p01 = image.Pixels[y1 * stride + x0 * 4 + channel];
        var p11 = image.Pixels[y1 * stride + x1 * 4 + channel];
        var top = p00 + (p10 - p00) * wx;
        var bottom = p01 + (p11 - p01) * wx;
        return top + (bottom - top) * wy;
    }

    private static float IntersectionOverUnion(VisionRectF a, VisionRectF b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        if (intersection <= 0)
            return 0;

        var union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static int ResolveIntraOpThreads()
    {
        var configured = Environment.GetEnvironmentVariable("TLBB_AGENT_ONNX_THREADS");
        return int.TryParse(configured, out var value) && value is >= 1 and <= 32
            ? value
            : Math.Clamp(Environment.ProcessorCount, 1, 2);
    }

    private static void ValidateImage(CaptureResult image)
    {
        if (image.Width <= 0 || image.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(image), "Image dimensions must be positive.");
        if (image.Pixels.Length < image.Width * image.Height * 4)
            throw new ArgumentException("CaptureResult must contain BGRX pixels.", nameof(image));
    }

    private readonly record struct LetterboxTransform(float Ratio, int PadX, int PadY);
}
