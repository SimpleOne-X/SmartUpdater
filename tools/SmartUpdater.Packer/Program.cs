using Aprillz.MewUI;
using Aprillz.MewUI.Platform;

namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>图形界面入口。</summary>
internal static class Program
{
    [STAThread]
    internal static void Main()
    {
        Win32Platform.Register();
        Direct2DBackend.Register();
        Application.Run(new MainView().Build());
    }
}
