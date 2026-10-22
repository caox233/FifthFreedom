using System.Threading;
using System.Windows.Forms;

public sealed class UpdaterProgressWindowTests
{
    [Fact]
    public void Window_uses_branded_visible_progress_instead_of_default_black_dialog()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new UpdaterProgressWindow();

                Assert.Equal(FormBorderStyle.None, window.FormBorderStyle);
                Assert.Equal("SCBL 更新进度 / SCBL update progress", window.AccessibleName);
                Assert.Single(window.Controls.Find("UpdateActivity", true));
                Assert.Single(window.Controls.Find("UpdateAccent", true));
                Assert.Equal(
                    "正在更新 / Updating",
                    Assert.IsType<Label>(window.Controls.Find("UpdateTitle", true).Single()).Text);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
            throw failure;
    }
}
