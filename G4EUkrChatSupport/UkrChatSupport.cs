using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using UkrChatSupportPlugin.Sys;
using UkrChatSupportPlugin.Windows;

namespace UkrChatSupportPlugin;

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class UkrChatSupport : IDalamudPlugin
{
    private int currentThreadId;
    private volatile uint foregroundThreadId;
    private bool isDisposed;
    private volatile bool isHooked;
    private KeyboardHook? keyboardHook;

    private CancellationTokenSource? stopToken;

#pragma warning disable CS8618
    public UkrChatSupport(
        IDalamudPluginInterface pluginInterface,
        IChatGui chatGui,
        IFramework framework,
        IPluginLog pluginLog)
    {
        PluginInterface = pluginInterface;
        Framework = framework;
        Chat = chatGui;
        PluginLog = pluginLog;

        Framework.RunOnFrameworkThread(Setup);
    }
#pragma warning restore CS8618

    private IDalamudPluginInterface PluginInterface { get; }
    private IFramework Framework { get; }
    public IPluginLog PluginLog { get; }
    public IChatGui Chat { get; }
    public WindowSystem WindowSystem { get; private set; }
    public Configuration Configuration { get; private set; }
    private ConfigWindow ConfigWindow { get; set; }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;

        stopToken?.Cancel();
        stopToken?.Dispose();
        stopToken = null;

        Chat.CheckMessageHandled -= ChatOnCheckMessageHandled;
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUI;
        PluginInterface.UiBuilder.OpenMainUi -= DrawConfigUI;
        WindowSystem?.RemoveAllWindows();
        if (Configuration != null) Configuration.OnConfigChanged -= Handle_ConfigurationOnOnConfigChanged;

        StopHook();
    }

    private void Setup()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);
        Configuration.OnConfigChanged += Handle_ConfigurationOnOnConfigChanged;

        WindowSystem = new WindowSystem("G4EUkrChatSupport");
        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;
        PluginInterface.UiBuilder.OpenMainUi += DrawConfigUI;

        Chat.CheckMessageHandled += ChatOnCheckMessageHandled;

        currentThreadId = KeyboardHook.GetCurrentThreadId();

        // The foreground window tracker is needed for every mode: it decides whether the game window
        // is active (input replacement) and which keyboard layout is in use.
        InitCheckerThread();
    }

    private void Handle_ConfigurationOnOnConfigChanged(Configuration configuration)
    {
#if DEBUG
        WriteCurrentConfig();
#endif
    }

    private void WriteCurrentConfig()
    {
        PluginLog.Information($"Configuration.ReactOnlyToUkLayout - {Configuration.ReactOnlyToUkLayout}");
        PluginLog.Information($"Configuration.ReplaceOnlyOnUkLayout - {Configuration.ReplaceOnlyOnUkLayout}");
        PluginLog.Information($"Configuration.ReplaceInput - {Configuration.ReplaceInput}");
    }

    private void Handle_keyboardHookOnKeyDown(Constants.Keys key, bool shift, bool ctrl, bool alt, ref bool skipNext)
    {
        try
        {
            if (ctrl || alt ||
                !IsInsideFFXIV() ||
                !Configuration.ReplaceInput ||
                !IsTyping() ||
                (Configuration.ReplaceOnlyOnUkLayout && !IsUkrainianLayout())) return;

            ReplaceInput(key, shift, ref skipNext);
        }
        catch (Exception e)
        {
            PluginLog.Error(e, e.Message);
        }
    }

    private static void ReplaceInput(Constants.Keys aKey, bool aIsShift, ref bool aNextSkip)
    {
        foreach (var keyReplace in Constants.ReplaceKeys.Where(keyReplace => keyReplace.Key == aKey))
        {
            aNextSkip = true;
            KeyboardHook.SendCharUnicode(aIsShift ? keyReplace.RCapitalKey : keyReplace.RKey);
            return;
        }
    }

    private void Handle_keyboardHook_OnError(Exception e)
    {
        PluginLog.Error(e, e.Message);
    }

    private void InitCheckerThread()
    {
        stopToken = new CancellationTokenSource();
        var token = stopToken.Token;
        var backgroundThread = new Thread(() => BackgroundWorker(token))
        {
            IsBackground = true,
            Name = "G4EUkrChatSupport foreground window thread"
        };
        backgroundThread.Start();
    }

    private void ChatOnCheckMessageHandled(IHandleableChatMessage chatMessage)
    {
        try
        {
            if (chatMessage.IsHandled || !IsChatTypeSupported(chatMessage.LogKind)) return;
            if (Configuration.ReactOnlyToUkLayout && !IsUkrainianLayout()) return;

            var replaced = ReplaceSymbols(chatMessage.Message);
            if (replaced != null) chatMessage.Message = replaced;
        }
        catch (Exception e)
        {
            PluginLog.Error(e, e.Message);
        }
    }

    private bool IsUkrainianLayout()
    {
        var currentLayout = NativeMethods.GetCurrentKeyboardLayout(foregroundThreadId);
        // "uk" - ukrainian
        return currentLayout.TwoLetterISOLanguageName.Equals("uk");
    }

    private static bool IsChatTypeSupported(XivChatType type)
    {
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        switch (type)
        {
            case XivChatType.None:
            case XivChatType.Debug:
            case XivChatType.Urgent:
            case XivChatType.Notice:
            case XivChatType.SystemError:
            case XivChatType.SystemMessage:
            case XivChatType.GatheringSystemMessage:
            case XivChatType.ErrorMessage:
            case XivChatType.NPCDialogue:
            case XivChatType.NPCDialogueAnnouncements:
            case XivChatType.RetainerSale:
                return false;
            default:
                return true;
        }
    }

    private bool IsInsideFFXIV()
    {
        return foregroundThreadId == (uint)currentThreadId;
    }

    private void StartHook()
    {
        Framework.RunOnFrameworkThread(() =>
        {
            if (isDisposed || isHooked || keyboardHook is not null) return;
            try
            {
                keyboardHook = new KeyboardHook(true);
                keyboardHook.KeyDown += Handle_keyboardHookOnKeyDown;
                keyboardHook.OnError += Handle_keyboardHook_OnError;
                isHooked = true;
            }
            catch (Exception e)
            {
                PluginLog.Error(e, e.Message);
            }
        });
    }

    private void StopHook()
    {
        Framework.RunOnFrameworkThread(() =>
        {
            if (!isHooked || keyboardHook is null) return;
            try
            {
                keyboardHook.KeyDown -= Handle_keyboardHookOnKeyDown;
                keyboardHook.OnError -= Handle_keyboardHook_OnError;
                keyboardHook.Dispose();
            }
            catch (Exception e)
            {
                PluginLog.Error(e, e.Message);
            }
            finally
            {
                keyboardHook = null;
                isHooked = false;
            }
        });
    }

    /// <returns>A new message with replaced symbols, or null if nothing had to be replaced.</returns>
    private static SeString? ReplaceSymbols(SeString message)
    {
        var changed = false;
        var payloads = message.Payloads.Select(payload =>
        {
            if (payload is not TextPayload { Text: { } input } || string.IsNullOrWhiteSpace(input)) return payload;

            var output = input
                         .Replace("і", "i")       // \u0456 to \u0069
                         .Replace("І", "I")       // \u0406 to \u0049
                         .Replace("ї", "ï")       // \u0457 to \u00EF
                         .Replace("Ї", "Ï")       // \u0407 to \u00CF
                         .Replace("є", "\u2208")  // \u0454 to \u2208
                         .Replace("Є", "\u2208"); // \u0404 to \u2208

            if (output == input) return payload;
            changed = true;
            return new TextPayload(output);
        }).ToList();

        return changed ? new SeString(payloads) : null;
    }

    private static unsafe bool IsTyping()
    {
        var atkModule = RaptureAtkModule.Instance();
        return atkModule != null && atkModule->IsTextInputActive();
    }

    private void GetForeground()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        foregroundThreadId = NativeMethods.GetWindowThreadProcessId(foregroundWindow, nint.Zero);
        if (IsInsideFFXIV())
            StartHook();
        else
            StopHook();
    }

    private void BackgroundWorker(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                GetForeground();
                Task.Delay(1000, token).Wait(token);
            }
        }
        catch (OperationCanceledException) { }
        catch (AggregateException ae) when (ae.InnerException is OperationCanceledException) { }
        catch (Exception e)
        {
            PluginLog.Error(e, e.Message);
        }
    }

    private void DrawUI()
    {
        WindowSystem.Draw();
    }

    public void DrawConfigUI()
    {
        ConfigWindow.Toggle();
    }
}
