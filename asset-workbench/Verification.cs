using System.IO;
using System.Text.Json;
using AssetsTools.NET.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CardAssetWorkbench;

public static class Verification
{
    public static void Run(string output)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"manifest.json")));
        var results=new List<object>();
        foreach(var b in doc.RootElement.GetProperty("Bundles").EnumerateArray())
        {
            var bundle=b.GetProperty("Bundle").Deserialize<BundleRecord>()!;
            var dir=Path.Combine(output,"bundles",Path.GetFileName(bundle.Path));
            var copy=Path.Combine(dir,"original.bundle");var hash=b.GetProperty("Sha256").GetString();
            if(AssetIndex.Sha(copy)!=hash||AssetIndex.Sha(bundle.Path)!=hash)throw new IOException("原文件/副本哈希不一致："+bundle.Path);
            var am=AssetIndex.Manager();
            try
            {
                var loaded=am.LoadBundleFile(copy,true);
                foreach(var ex in b.GetProperty("Exports").EnumerateArray())
                {
                    var asset=ex.GetProperty("Asset").Deserialize<AssetRecord>()!;
                    var path=Path.Combine(output,ex.GetProperty("File").GetString()!);
                    if(AssetIndex.Sha(path)!=ex.GetProperty("Sha256").GetString())throw new IOException("导出文件哈希不一致。");
                    if(asset.Type!="Texture2D")continue;
                    var af=am.LoadAssetsFileFromBundle(loaded,asset.FileIndex,false);am.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
                    var field=am.GetBaseField(af,af.file.GetAssetInfo(asset.PathId));
                    var texture=TextureFile.ReadTextureFile(field);texture.FillPictureData(af);
                    var pixels=texture.DecodeTextureRaw(texture.pictureData)!;
                    using var png=Image.Load<Bgra32>(path);
                    if(png.Width!=texture.m_Width||png.Height!=texture.m_Height)throw new IOException("贴图尺寸不一致。");
                    png.Mutate(ctx=>ctx.Flip(FlipMode.Vertical));var decoded=new byte[pixels.Length];png.CopyPixelDataTo(decoded);
                    if(!pixels.SequenceEqual(decoded))throw new IOException("贴图导出像素/透明度不一致："+path);
                    results.Add(new{Texture=asset.Name,asset.Width,asset.Height,Passed=true,Comparison="PNG 逆向翻转后，逐字节与原 Bundle 解码 BGRA 一致（包含 Alpha）"});
                }
            }
            finally{am.UnloadAll();}
        }
        AssetIndex.Save(Path.Combine(output,"提取验证.json"),new{TimeUtc=DateTime.UtcNow,OriginalAndCopyHashes="全部一致",Textures=results,GameWrite=false});
        Console.WriteLine($"验证通过：{results.Count} 张贴图逐像素一致，全部原文件与副本 SHA-256 一致。");
    }
}
