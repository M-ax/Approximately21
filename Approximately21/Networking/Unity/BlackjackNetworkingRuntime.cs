using System;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Steamworks;
using Unity.Entities;
using UnityEngine;

namespace Approximately21.Networking.Unity;

public sealed class BlackjackNetworkingRuntime : MonoBehaviour
{
    internal static int Channel = 21021;
    public static BlackjackNetworkingRuntime Instance { get; private set; }
    private Core _core;
    private World _world;
    private SteamLobbyAdapter _lobby;
    private Harmony _patches;
    private bool _failed;
    private IntPtr _stoppedSteam;
    private bool _steamShutdown;
    private bool _hadLobby;
    private bool _hooksReady;
    private float _retryAt;
    private Action _serviceChanged;
    public BlackjackStateService Service { [HideFromIl2Cpp] get; [HideFromIl2Cpp] private set; }
    public BlackjackTableRegistry Registry { [HideFromIl2Cpp] get; [HideFromIl2Cpp] private set; }
    public event Action ServiceChanged
    {
        [HideFromIl2Cpp] add => _serviceChanged += value;
        [HideFromIl2Cpp] remove => _serviceChanged -= value;
    }
    public ulong LocalPlayerId { get; private set; }
    public string Feedback { get; private set; } = "Waiting for game session.";

    public BlackjackNetworkingRuntime(IntPtr pointer) : base(pointer) { }

    private void Awake()
    {
        Instance = this;
        _patches = new Harmony("Approximately21.Networking.Lifecycle");
        try
        {
            var prefix = new HarmonyMethod(typeof(BlackjackNetworkingRuntime), nameof(BeforeSteamShutdown));
            _patches.Patch(AccessTools.Method(typeof(SteamManager), nameof(SteamManager.Dispose)), prefix: prefix);
            _patches.Patch(AccessTools.Method(typeof(SteamManager), nameof(SteamManager.ShutdownEverything)), prefix: prefix);
            _patches.Patch(AccessTools.Method(typeof(SteamAPI), nameof(SteamAPI.Shutdown)),
                prefix: new HarmonyMethod(typeof(BlackjackNetworkingRuntime), nameof(BeforeApiShutdown)));
            _patches.Patch(AccessTools.Method(typeof(Core), nameof(Core.Dispose)),
                prefix: new HarmonyMethod(typeof(BlackjackNetworkingRuntime), nameof(BeforeCoreDispose)));
            _hooksReady = true;
        }
        catch (Exception exception)
        {
            Feedback = "Blackjack lifecycle hooks unavailable; networking disabled.";
            Plugin.LogError($"{Feedback} {exception}");
        }
    }

    [HideFromIl2Cpp]
    private static void BeforeApiShutdown()
    {
        if (Instance == null)
            return;
        Instance._steamShutdown = true;
        Instance.StopSession();
    }

    [HideFromIl2Cpp]
    private static void BeforeCoreDispose()
    {
        if (Instance != null)
            Instance.StopSession();
    }

    [HideFromIl2Cpp]
    private static void BeforeSteamShutdown(SteamManager __instance)
    {
        if (Instance == null)
            return;
        Instance._stoppedSteam = __instance.Pointer;
        Instance.StopSession();
    }

    private void Update()
    {
        try
        {
            if (!_hooksReady || _steamShutdown || Time.unscaledTime < _retryAt)
                return;
            var core = Core.Get();
            var world = World.DefaultGameObjectInjectionWorld;
            if (core == null || core._steam == null || world == null || !world.IsCreated)
            {
                StopSession();
                return;
            }
            if (_stoppedSteam == core._steam.Pointer)
            {
                if (core._steam._lobbyState != SteamManager.LobbyState.Created &&
                    core._steam._lobbyState != SteamManager.LobbyState.Connected)
                    return;
                _stoppedSteam = IntPtr.Zero;
            }
            if (_core == null || _core.Pointer != core.Pointer || _world == null ||
                _world.Pointer != world.Pointer || !_world.IsCreated)
            {
                StopSession();
                _core = core;
                _world = world;
                var transport = new SteamGameTransport(Channel);
                Service = new BlackjackStateService(transport);
                _lobby = new SteamLobbyAdapter(core._steam, transport);
                Registry = new BlackjackTableRegistry(world, Service);
                _serviceChanged?.Invoke();
                Plugin.LogInfo($"Blackjack networking channel {Channel}; all peers must configure the same unused channel.");
            }
            var lobby = _lobby.ReadLobby();
            LocalPlayerId = lobby?.LocalPlayerId ?? 0;
            if (lobby != null || _hadLobby)
                Service.SetLobby(lobby);
            _hadLobby = lobby != null;
            if (lobby != null)
            {
                Registry.Pump();
                Service.Pump();
            }
            Feedback = lobby == null ? "Waiting for verified lobby/host identity." :
                Service.IsReady ? "" : "Synchronizing with host.";
            _failed = false;
        }
        catch (Exception exception)
        {
            if (!_failed)
                Plugin.LogError($"Blackjack networking unavailable: {exception}");
            _failed = true;
            _retryAt = Time.unscaledTime + 5f;
            StopSession();
            Feedback = "Blackjack networking unavailable; see plugin log.";
        }
    }

    [HideFromIl2Cpp]
    private void StopSession()
    {
        var hadService = Service != null;
        _lobby?.Dispose();
        _lobby = null;
        Registry?.Dispose();
        Registry = null;
        Service?.Dispose();
        Service = null;
        _core = null;
        _world = null;
        _hadLobby = false;
        LocalPlayerId = 0;
        Feedback = "Waiting for game session.";
        if (hadService)
            _serviceChanged?.Invoke();
    }

    private void OnDestroy()
    {
        StopSession();
        _patches?.UnpatchSelf();
        _serviceChanged = null;
        if (Instance == this)
            Instance = null;
    }
}