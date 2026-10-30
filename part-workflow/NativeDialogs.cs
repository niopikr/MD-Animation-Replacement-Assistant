using System.Runtime.InteropServices;
namespace PartWorkflow;
public static class NativeDialogs
{
    public static Task<string?> PickGameFolder()
    {
        var result=new TaskCompletionSource<string?>();
        var thread=new Thread(()=>
        {
            object? shell=null,folder=null;
            try
            {
                var type=Type.GetTypeFromProgID("Shell.Application")??throw new InvalidOperationException("Windows文件夹选择器不可用，请直接输入游戏路径。");
                shell=Activator.CreateInstance(type);dynamic browser=shell!;folder=browser.BrowseForFolder(0,"选择包含 masterduel.exe 的游戏目录",0x51,0);
                if(folder is null){result.SetResult(null);return;}
                dynamic picked=folder;string path=picked.Self.Path;result.SetResult(Directory.Exists(path)?path:null);
            }
            catch(Exception ex){result.SetException(ex);}
            finally{if(folder is not null)Marshal.FinalReleaseComObject(folder);if(shell is not null)Marshal.FinalReleaseComObject(shell);}
        });thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();return result.Task;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int MessageBoxW(nint hwnd,string text,string caption,uint type);
    public static void Error(string text)=>MessageBoxW(0,text,"MD动画替换助手",0x10);
}
