using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SimpleOneX.SmartUpdater.Samples.WinForms;

/// <summary>
/// 进程内自渲染截图。用 PrintWindow(PW_RENDERFULLCONTENT) 而不是 CopyFromScreen：
/// 锁屏时 CopyFromScreen 截到的是锁屏壁纸，而 PrintWindow 仍能拿到窗口自己的内容。
/// 也不用 Control.DrawToBitmap：它会在 AntdUI 自绘的标题栏上再画一层系统经典标题栏。
/// </summary>
internal static class WindowShot
{
    private const uint PwRenderFullContent = 2;
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    // 用 GetWindowLongW（32 位返回值）而不是 GetWindowLongPtrW：后者在 x86 上没有导出；GWL_EXSTYLE 本来就只有 32 位。
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    /// <summary>把当前所有可见的非分层窗体合成为一张 PNG。必须在 UI 线程调用。失败不抛出，返回 false 并给出原因。</summary>
    public static bool TrySave(string path, out string? error)
    {
        error = null;
        try
        {
            List<Form> open = [.. Application.OpenForms.Cast<Form>()];

            // 最小化时 PrintWindow 得到黑图、GetWindowRect 只有 199x34：先还原主窗体。
            if (open.Count > 0 && open[0].WindowState == FormWindowState.Minimized)
            {
                open[0].WindowState = FormWindowState.Normal;
            }

            // 跳过分层窗体是关键：AntdUI.LayeredFormMask（弹窗遮罩）是分层的，PrintWindow 对它只渲染出一片纯色，
            // 合成时会把主窗口整个盖掉；LayeredFormModal（弹窗本体）不是分层窗体，能正常渲染。
            List<Form> forms = [.. open.Where(f => f.Visible && !IsLayered(f))];
            if (forms.Count == 0)
            {
                error = "没有可截图的可见窗体";
                return false;
            }

            Rectangle union = forms[0].Bounds;
            foreach (Form f in forms)
            {
                union = Rectangle.Union(union, f.Bounds);
            }

            using var canvas = new Bitmap(union.Width, union.Height);
            using (Graphics target = Graphics.FromImage(canvas))
            {
                target.Clear(Color.White);
                foreach (Form f in forms)
                {
                    using var piece = new Bitmap(f.Width, f.Height);
                    using (Graphics g = Graphics.FromImage(piece))
                    {
                        IntPtr hdc = g.GetHdc();
                        try
                        {
                            PrintWindow(f.Handle, hdc, PwRenderFullContent);
                        }
                        finally
                        {
                            g.ReleaseHdc(hdc);
                        }
                    }

                    target.DrawImageUnscaled(piece, f.Bounds.X - union.X, f.Bounds.Y - union.Y);
                }
            }

            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            canvas.Save(path, ImageFormat.Png);
            return true;
        }
        catch (Exception ex)
        {
            // 截不到图不该让示例崩溃：任何异常都转成 error 文本。
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool IsLayered(Form form) => (GetWindowLong(form.Handle, GwlExStyle) & WsExLayered) != 0;
}
