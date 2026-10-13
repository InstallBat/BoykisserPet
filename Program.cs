namespace BoykisserPet;

internal static class Program
{
    private const string MutexName = "BoykisserPet.SingleInstance.v1";

    [STAThread]
    private static void Main()
    {
        // A second copy would put a duplicate pet on screen and fight over the
        // settings file, so refuse to start twice.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Boykisser Pet is already running.\nLook for it in the notification area.",
                "Boykisser Pet",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        using var context = new PetAppContext();
        Application.Run(context);

        GC.KeepAlive(mutex);
    }
}
