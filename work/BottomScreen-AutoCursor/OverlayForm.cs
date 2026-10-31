namespace BottomScreen;

internal sealed class OverlayForm : Form
{
    public OverlayForm(Rectangle bounds, bool inputTransparent = true, bool excludeFromCapture = true)
    {
        InputTransparent = inputTransparent;
        ExcludeFromCapture = excludeFromCapture;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Text = "BottomScreen output";
    }
    private bool InputTransparent { get; }
    private bool ExcludeFromCapture { get; }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            // Layered+transparent makes input pass through to the original game, including across threads.
            p.ExStyle |= 0x08000000 | 0x00000080 | 0x00200000;
            if (InputTransparent) p.ExStyle |= 0x00080000 | 0x00000020;
            return p;
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (InputTransparent) Native.SetLayeredWindowAttributes(Handle, 0, 255, 2);
        if (ExcludeFromCapture) Native.SetWindowDisplayAffinity(Handle, 0x11);
    }
    protected override void WndProc(ref Message m)
    {
        if (InputTransparent && m.Msg == 0x0084) { m.Result = -1; return; }
        if (m.Msg == 0x0021) { m.Result = 3; return; }
        base.WndProc(ref m);
    }
}
