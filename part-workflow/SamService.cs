using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;

public sealed class SamService(ProjectStore store) : IDisposable
{
    readonly SemaphoreSlim _gate = new(1);
    Sam2PointCutoutService? _sam;
    Sam2PointCutoutService.ImageSession? _image;
    string _imagePath = "";
    public string ModelDirectory { get {var path=Path.Combine(store.Workspace,"资源","models","sam2");return File.Exists(Path.Combine(path,Sam2PointCutoutService.Sam21EncoderFileName))&&File.Exists(Path.Combine(path,Sam2PointCutoutService.Sam21DecoderFileName))?path:"";} }
    public async Task<byte[]> Predict(ProjectState state, SamRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            if (ModelDirectory.Length == 0) throw new InvalidOperationException("未找到本地 SAM 2.1 模型，仍可使用套索。不会自动下载模型。");
            _sam ??= new Sam2PointCutoutService(ModelDirectory);
            var artPath = ProjectStore.Under(store.DirectoryFor(state.Id), state.Artwork);
            if (_imagePath != artPath)
            {
                _image?.Dispose(); _image = null; _imagePath = "";
                using var art = Image.Load<Rgba32>(artPath);
                _image = await _sam.CreateImageSessionAsync(art);
                _imagePath = artPath;
            }
            Image<L8> result;
            if (request.Points.Length > 0)
                result = await _image!.PredictWithPromptsAsync(request.Points.Select(p => new Sam2PointCutoutService.PromptPoint(p[0], p[1], p[2])).ToArray());
            else
            {
                using var rgba = ProjectStore.DecodePng(request.Mask);
                using var mask = new Image<L8>(rgba.Width, rgba.Height);
                for (var y = 0; y < rgba.Height; y++) for (var x = 0; x < rgba.Width; x++) mask[x, y] = new L8(rgba[x, y].A);
                result = await _image!.PredictFromPaintAsync(mask);
            }
            using (result)
            using (var rgba = Atlas.MaskRgba(result))
            using (var stream = new MemoryStream())
            {
                if (request.LimitToHint)
                {
                    using var hint = ProjectStore.DecodePng(request.Mask);
                    if (hint.Size != rgba.Size) throw new InvalidDataException("提示范围尺寸错误");
                    for (var y = 0; y < rgba.Height; y++) for (var x = 0; x < rgba.Width; x++)
                    {
                        var c = rgba[x,y]; c.A = (byte)((c.A * hint[x,y].A + 127) / 255); rgba[x,y] = c;
                    }
                }
                rgba.SaveAsPng(stream); return stream.ToArray();
            }
        }
        finally { _gate.Release(); }
    }
    public void Dispose() { _image?.Dispose(); _sam?.Dispose(); _gate.Dispose(); }
}
