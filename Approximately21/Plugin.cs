using Approximately21.Blackjack;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace Approximately21;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal new static ManualLogSource Log;

    internal static void LogInfo(object data) => Log.LogInfo($"♠️ {data}");

    internal static void LogWarning(object data) => Log.LogWarning($"♠️ {data}");

    internal static void LogError(object data) => Log.LogError($"♠️ {data}");

    public override void Load()
    {
        Log = base.Log;
        LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        BlackjackTable.RegisterDefinition();
        AddComponent<BlackjackTable>();
    }
}