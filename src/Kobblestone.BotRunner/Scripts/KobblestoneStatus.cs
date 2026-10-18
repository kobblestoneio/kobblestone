//MCCScript 1.0

MCC.LoadBot(new KobblestoneStatus());

//MCCScript Extensions

// Bundled with the Kobblestone bot image and loaded on the first login via the ScriptScheduler.
// Marks the container ready while the bot is in the game; the Pod's readiness probe checks the file.
// Kobblestone.BotRunner passes the path of the file (BotRunnerOptions.ReadyFile) in KOBBLESTONE_READY_FILE.
public class KobblestoneStatus : ChatBot
{
    private static readonly string ReadyFile =
        Environment.GetEnvironmentVariable("KOBBLESTONE_READY_FILE") ?? "/tmp/kobblestone-ready";

    public override void Initialize()
    {
        // Loaded after the first login, so the bot is already in the game.
        MarkReady();
    }

    public override void AfterGameJoined()
    {
        MarkReady();
    }

    public override bool OnDisconnect(DisconnectReason reason, string message)
    {
        try
        {
            File.Delete(ReadyFile);
        }
        catch (IOException)
        {
        }

        // Do not handle the disconnect: MCC exits and Kubernetes restarts the container.
        return false;
    }

    private static void MarkReady()
    {
        File.WriteAllText(ReadyFile, DateTime.UtcNow.ToString("O"));
    }
}
