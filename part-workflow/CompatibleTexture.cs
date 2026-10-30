using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PartWorkflow;

public static class CompatibleTexture
{
    public static List<GameReplacementExporter.Edit> Prepare(ProjectStore store,ProjectState p,Atlas atlas,
        string model,Dictionary<string,GameReplacementExporter.Locator> assets)
    {
        var edits=new List<GameReplacementExporter.Edit>();var root=store.DirectoryFor(p.Id);
        foreach(var pageName in atlas.Pages.Keys)
        {
            using var page=Image.Load<Rgba32>(Path.Combine(root,"source",pageName));
            foreach(var part in p.Parts.Where(x=>x.Page==pageName&&x.Status is "replaced" or "empty"))
            {
                var rev=JsonFile.Read<Revision>(ProjectStore.Under(root,part.ConfirmedRevision));
                using var replacement=Image.Load<Rgba32>(ProjectStore.Under(root,rev.Aligned));
                if(replacement.Width!=part.Width||replacement.Height!=part.Height)throw new InvalidDataException("确认部件尺寸不一致");
                if(part.Pma)Atlas.ToPma(replacement);
                using var packed=Atlas.Pack(replacement,part);
                using var mask=Atlas.MeshMask(part);using var maskRgba=Atlas.MaskRgba(mask);using var packedMask=Atlas.Pack(maskRgba,part);
                // Update every pixel owned by this mesh, including transparent holes. Original atlas layout stays fixed.
                for(var y=0;y<packed.Height;y++)for(var x=0;x<packed.Width;x++)
                    if(packedMask[x,y].A>0)page[x+part.Bounds[0],y+part.Bounds[1]]=packed[x,y];
            }
            var png=Path.Combine(model,pageName);JsonFile.Png(png,page);
            if(assets.TryGetValue(pageName,out var asset))edits.Add(new(asset,png,null));
        }
        File.Copy(Path.Combine(root,"source",p.AtlasFile),Path.Combine(model,p.AtlasFile));
        return edits;
    }
    static AssetsManager Manager(string classData){var am=new AssetsManager();am.LoadClassPackage(classData);return am;}
    public static object Rewrite(string path,GameReplacementExporter.Edit[] edits)
    {
        var classData=AppEnvironment.Resource("classdata.tpk");var am=Manager(classData);
        var raw=path+".compat-raw";var packed=path+".compat-packed";
        var expectedEntries=new Dictionary<int,byte[]>();var names=new List<string>();var reports=new List<object>();
        try
        {
            var bundle=am.LoadBundleFile(path,true);
            // Save expected entry bytes. Only blocks in the resS entries may change; all CAB metadata stays byte-identical.
            for(var i=0;i<bundle.file.BlockAndDirInfo.DirectoryInfos.Count;i++)
            {names.Add(bundle.file.BlockAndDirInfo.DirectoryInfos[i].Name);bundle.file.GetFileRange(i,out var offset,out var size);bundle.file.DataReader.Position=offset;expectedEntries[i]=bundle.file.DataReader.ReadBytes(checked((int)size));}
            var changedStreams=new HashSet<int>();
            foreach(var edit in edits)
            {
                var af=am.LoadAssetsFileFromBundle(bundle,edit.Asset.FileIndex,false);am.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
                var info=af.file.GetAssetInfo(edit.Asset.PathId);var f=am.GetBaseField(af,info);var tex=TextureFile.ReadTextureFile(f);
                if(f["m_Name"].AsString!=edit.Asset.Name||tex.m_TextureFormat!=(int)TextureFormat.BC7||tex.m_MipCount!=1)
                    throw new InvalidDataException("兼容测试版仅支持原始BC7、单Mipmap贴图，未作格式降级。");
                using var target=Image.Load<Rgba32>(edit.Png!);
                if(target.Width!=tex.m_Width||target.Height!=tex.m_Height||tex.m_Width%4!=0||tex.m_Height%4!=0)throw new InvalidDataException("兼容导出要求原贴图尺寸完全一致且4像素对齐");
                tex.FillPictureData(af);var originalCompressed=tex.pictureData;var compressed=(byte[])originalCompressed.Clone();
                var decoded=tex.DecodeTextureRaw(originalCompressed)!;
                var encoder=new BcEncoder(CompressionFormat.Bc7);encoder.OutputOptions.Quality=CompressionQuality.Balanced;encoder.OutputOptions.GenerateMipMaps=false;
                var pixels=new ColorRgba32[16];var changed=0;
                for(var by=0;by<tex.m_Height/4;by++)for(var bx=0;bx<tex.m_Width/4;bx++)
                {
                    var different=false;
                    for(var j=0;j<4;j++)for(var i=0;i<4;i++)
                    {
                        var x=bx*4+i;var unityY=by*4+j;var c=target[x,tex.m_Height-1-unityY];var k=(unityY*tex.m_Width+x)*4;
                        if(c.R!=decoded[k+2]||c.G!=decoded[k+1]||c.B!=decoded[k]||c.A!=decoded[k+3])different=true;
                        pixels[j*4+i]=new ColorRgba32(c.R,c.G,c.B,c.A);
                    }
                    if(!different)continue;
                    var block=encoder.EncodeBlock(pixels);if(block.Length!=16)throw new InvalidDataException("BC7块尺寸错误");
                    block.CopyTo(compressed,(by*(tex.m_Width/4)+bx)*16);changed++;
                }
                var stream=f["m_StreamData"];var streamName=stream["path"].AsString.Replace('\\','/').Split('/').Last();
                var streamIndex=names.FindIndex(n=>n.Replace('\\','/').Split('/').Last()==streamName);
                var streamOffset=checked((int)stream["offset"].AsULong);var streamSize=stream["size"].AsUInt;
                if(streamName.Length==0||streamIndex<0||streamSize!=compressed.Length||streamOffset+compressed.Length>expectedEntries[streamIndex].Length)
                    throw new InvalidDataException("不能在原resS布局内等长替换，未改变原资源结构。");
                compressed.CopyTo(expectedEntries[streamIndex],streamOffset);changedStreams.Add(streamIndex);
                // Save the actual BC7-decoded image for the in-app preview, including compression effects.
                var actual=tex.DecodeTextureRaw(compressed)!;using var actualImage=Image.LoadPixelData<Bgra32>(actual,tex.m_Width,tex.m_Height);
                actualImage.Mutate(c=>c.Flip(SixLabors.ImageSharp.Processing.FlipMode.Vertical));
                using var rgba=actualImage.CloneAs<Rgba32>();JsonFile.Png(edit.Png!,rgba);
                reports.Add(new{Name=tex.m_Name,Width=tex.m_Width,Height=tex.m_Height,Format="BC7",MipCount=tex.m_MipCount,StreamPath=stream["path"].AsString,StreamOffset=streamOffset,StreamSize=streamSize,ChangedBlocks=changed,OriginalBlocks=(tex.m_Width/4)*(tex.m_Height/4)-changed});
            }
            foreach(var index in changedStreams)bundle.file.BlockAndDirInfo.DirectoryInfos[index].SetNewData(expectedEntries[index]);
            using(var writer=new AssetsFileWriter(raw))bundle.file.Write(writer);
            am.UnloadAll();am=Manager(classData);var pack=am.LoadBundleFile(raw);using(var writer=new AssetsFileWriter(packed))pack.file.Pack(writer,AssetBundleCompressionType.LZ4);
            am.UnloadAll();am=Manager(classData);var verify=am.LoadBundleFile(packed,true);
            if(!verify.file.BlockAndDirInfo.DirectoryInfos.Select(d=>d.Name).SequenceEqual(names))throw new InvalidDataException("Bundle目录名称发生变化");
            for(var i=0;i<names.Count;i++){verify.file.GetFileRange(i,out var offset,out var size);verify.file.DataReader.Position=offset;var bytes=verify.file.DataReader.ReadBytes(checked((int)size));if(!bytes.SequenceEqual(expectedEntries[i]))throw new InvalidDataException("原格式回读字节不一致");}
            am.UnloadAll();File.Copy(packed,path,true);
            return new{Textures=reports,AllCabMetadataIdentical=true,OriginalStreamLayout=true,Verified=true};
        }
        finally{am.UnloadAll();if(File.Exists(raw))File.Delete(raw);if(File.Exists(packed))File.Delete(packed);}
    }
}
