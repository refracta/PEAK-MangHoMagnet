using BepInEx;
using BepInEx.Configuration;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace MangHoMagnet;

[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;
    internal static Plugin Instance { get; private set; } = null!;

    private static readonly Regex SteamLinkRegex = new Regex(
        @"steam://joinlobby/\d+/\d+/\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SteamLinkParseRegex = new Regex(
        @"steam://joinlobby/(?<app>\d+)/(?<lobby>\d+)/(?<host>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new Regex(
        @"<.*?>",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly string[] ModalBlockedPhrases =
    {
        "FAILED TO FIND LOBBY",
        "로비를 찾지 못했습니다",
        "FAILED TO FIND PHOTON",
        "FAILED TO FIND PHOTON ROOM",
        "PHOTON 로비를 찾지 못했습니다"
    };

    private readonly HashSet<string> _seenPostUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LobbyEntry> _lobbyByLink = new Dictionary<string, LobbyEntry>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, LobbyEntry> _lobbyById = new Dictionary<ulong, LobbyEntry>();
    private readonly List<LobbyEntry> _lobbyEntries = new List<LobbyEntry>();
    private readonly object _lobbyLock = new object();
    private readonly SemaphoreSlim _pollGate = new SemaphoreSlim(1, 1);
    private readonly Queue<ulong> _pendingLobbyChecks = new Queue<ulong>();
    private readonly Dictionary<string, PostInfo> _postInfoByUrl = new Dictionary<string, PostInfo>(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingLobbyLock = new object();

    private const int MaxLobbyChecksPerFrame = 4;
    private const float OpenPostWidth = 110f;
    private const float JoinButtonWidth = 260f;
    private const float ActionGapWidth = 10f;
    private const float ActionButtonSpacing = 6f;
    private const float ActionButtonHeight = 24f;
    private const float TableRightPadding = 12f;

    private HttpClient? _http;
    private Harmony? _harmony;
    private CancellationTokenSource? _cts;
    private DateTime _lastJoinUtc = DateTime.MinValue;
    private DateTime _lastPollUtc = DateTime.MinValue;
    private DateTime _nextPollUtc = DateTime.MinValue;

    private ConfigEntry<bool> _enabled = null!;
    private ConfigEntry<string> _galleryListUrl = null!;
    private ConfigEntry<string> _subjectKeyword = null!;
    private ConfigEntry<bool> _autoJoin = null!;
    private ConfigEntry<int> _pollIntervalSeconds = null!;
    private ConfigEntry<int> _maxPostsPerPoll = null!;
    private ConfigEntry<int> _joinCooldownSeconds = null!;
    private ConfigEntry<int> _maxLobbyEntries = null!;
    private ConfigEntry<bool> _validateLobbies = null!;
    private ConfigEntry<string> _validationMode = null!;
    private ConfigEntry<int> _validationIntervalSeconds = null!;
    private ConfigEntry<bool> _suppressLobbyPopups = null!;
    private ConfigEntry<int> _expectedAppId = null!;
    private ConfigEntry<string> _userAgent = null!;
    private ConfigEntry<bool> _logFoundLinks = null!;
    private ConfigEntry<KeyboardShortcut> _toggleKey = null!;
    private ConfigEntry<bool> _showUiOnStart = null!;
    private ConfigEntry<int> _uiWidth = null!;
    private ConfigEntry<int> _uiHeight = null!;

    private bool _showUi;
    private bool _inputBlockedByUs;
    private int _lobbyVersion;
    private int _uiVersion;
    private Vector2 _scrollPos;
    private Rect _windowRect = new Rect(20, 20, 1650, 620);
    private List<LobbyEntryView> _lobbySnapshot = new List<LobbyEntryView>();

    private bool _steamValidationEnabled;
    private Callback<LobbyDataUpdate_t>? _lobbyDataCallback;
    private bool _loggedListEmpty;
    private bool _loggedNoLinks;
    private DateTime _lastModalCheckUtc = DateTime.MinValue;

    private GUIStyle? _headerStyle;
    private GUIStyle? _cellStyle;
    private GUIStyle? _dateStyle;
    private GUIStyle? _linkStyle;
    private GUIStyle? _smallLabelStyle;
    private GUIStyle? _menuLabelStyle;
    private GUIStyle? _windowStyle;
    private GUIStyle? _headerBoxStyle;
    private GUIStyle? _rowBoxStyle;
    private GUIStyle? _buttonStyle;
    private GUIStyle? _joinButtonStyle;
    private GUIStyle? _joinButtonValidStyle;
    private GUIStyle? _joinButtonFullStyle;
    private GUIStyle? _joinButtonInvalidStyle;
    private GUIStyle? _toggleStyle;
    private Texture2D? _windowBackgroundTex;
    private Texture2D? _headerBackgroundTex;

    private void Awake()
    {
        Log = Logger;
        Instance = this;
        BindConfig();
        ApplyHarmonyPatches();
        TryInitializeSteamValidation();

        _showUi = _showUiOnStart.Value;
        SetNextPollUtc();

        _http = CreateHttpClient(_userAgent.Value);
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PollLoopAsync(_cts.Token));

        Log.LogInfo($"Plugin {Name} is loaded!");
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
        _http?.Dispose();
        _harmony?.UnpatchSelf();
    }

    private void Update()
    {
        if (_toggleKey.Value.IsDown())
        {
            ToggleUi();
        }

        if (_steamValidationEnabled)
        {
            try
            {
                SteamAPI.RunCallbacks();
            }
            catch
            {
            }

            ProcessLobbyChecks();
            TryDismissLobbyPopups();
        }
    }

    private void OnGUI()
    {
        if (!_showUi)
        {
            return;
        }

        EnsureGuiStyles();

        if (_windowRect.width != _uiWidth.Value || _windowRect.height != _uiHeight.Value)
        {
            _windowRect.width = _uiWidth.Value;
            _windowRect.height = _uiHeight.Value;
        }

        var version = Volatile.Read(ref _lobbyVersion);
        if (version != _uiVersion)
        {
            _lobbySnapshot = GetLobbySnapshot();
            _uiVersion = version;
        }

        _windowRect = GUILayout.Window(
            7813421,
            _windowRect,
            DrawWindowContents,
            "MangHoMagnet",
            _windowStyle ?? GUI.skin.window);
    }

    private void DrawWindowContents(int id)
    {
        var previousContentColor = GUI.contentColor;
        GUI.contentColor = Color.white;
        if (_headerStyle == null || _cellStyle == null || _linkStyle == null || _smallLabelStyle == null)
        {
            GUI.contentColor = previousContentColor;
            return;
        }

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Refresh Now", _buttonStyle ?? GUI.skin.button, GUILayout.Width(140)))
        {
            RequestManualPoll();
        }
        if (GUILayout.Button("Hide (F8)", _buttonStyle ?? GUI.skin.button, GUILayout.Width(110)))
        {
            ToggleUi();
        }
        GUILayout.Space(8f);
        _autoJoin.Value = GUILayout.Toggle(_autoJoin.Value, "Auto Join", _toggleStyle ?? GUI.skin.toggle);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        var menuStyle = _menuLabelStyle ?? _smallLabelStyle;
        GUILayout.Label($"Last poll: {(_lastPollUtc == DateTime.MinValue ? "-" : _lastPollUtc.ToLocalTime().ToString("HH:mm:ss"))}", menuStyle);
        GUILayout.Space(16f);
        var remainingSeconds = GetRemainingSeconds();
        var countdownText = _enabled.Value
            ? $"Next refresh: {remainingSeconds}s"
            : "Next refresh: paused";
        GUILayout.Label(countdownText, menuStyle);
        GUILayout.EndHorizontal();

        var scrollViewWidth = GetScrollViewWidth();
        var scrollViewContentWidth = GetScrollViewContentWidth(scrollViewWidth);
        var headerBox = _headerBoxStyle ?? GUI.skin.box;
        var rowBox = _rowBoxStyle ?? GUI.skin.box;
        var headerContentWidth = GetContentWidthForStyle(scrollViewContentWidth, headerBox);
        var rowContentWidth = GetContentWidthForStyle(scrollViewContentWidth, rowBox);
        var contentWidth = Mathf.Min(headerContentWidth, rowContentWidth);
        const float colIdWidth = 80f;
        const float colAuthorWidth = 170f;
        const float colDateWidth = 200f;
        const float colViewsWidth = 70f;
        var colActionsWidth = OpenPostWidth + JoinButtonWidth + ActionGapWidth + ActionButtonSpacing;
        var maxTableContentWidth = colIdWidth + colAuthorWidth + colDateWidth + colViewsWidth + colActionsWidth + 520f;
        var tableContentWidth = Mathf.Min(contentWidth - TableRightPadding, maxTableContentWidth);
        tableContentWidth = Mathf.Max(300f, tableContentWidth);
        var titleAvailable = tableContentWidth - (colIdWidth + colAuthorWidth + colDateWidth + colViewsWidth + colActionsWidth);
        var titleWidth = Mathf.Clamp(titleAvailable, 160f, 520f);
        if (titleWidth > titleAvailable)
        {
            titleWidth = Math.Max(80f, titleAvailable);
        }

        var headerBoxWidth = tableContentWidth + headerBox.padding.left + headerBox.padding.right;
        GUILayout.BeginHorizontal(headerBox, GUILayout.Width(headerBoxWidth), GUILayout.ExpandWidth(false));
        GUILayout.Label("번호", _headerStyle, GUILayout.Width(colIdWidth));
        GUILayout.Label("제목", _headerStyle, GUILayout.Width(titleWidth));
        GUILayout.Label("글쓴이", _headerStyle, GUILayout.Width(colAuthorWidth));
        GUILayout.Label("작성일", _headerStyle, GUILayout.Width(colDateWidth));
        GUILayout.Label("조회수", _headerStyle, GUILayout.Width(colViewsWidth));
        GUILayout.Space(colActionsWidth);
        GUILayout.EndHorizontal();

        _scrollPos.x = 0f;
        _scrollPos = GUILayout.BeginScrollView(
            _scrollPos,
            false,
            true,
            GUILayout.ExpandHeight(true),
            GUILayout.Width(scrollViewWidth),
            GUILayout.ExpandWidth(false));
        GUILayout.BeginVertical(GUILayout.Width(tableContentWidth), GUILayout.ExpandWidth(false));

        if (_lobbySnapshot.Count == 0)
        {
            GUILayout.Label("No lobby links yet.");
        }
        else
        {
            foreach (var entry in _lobbySnapshot)
            {
                DrawLobbyEntry(entry, colIdWidth, titleWidth, colAuthorWidth, colDateWidth, colViewsWidth, tableContentWidth, colActionsWidth);
            }
        }

        GUILayout.EndVertical();
        GUILayout.EndScrollView();

        GUI.DragWindow();
        GUI.contentColor = previousContentColor;
    }

    private void DrawLobbyEntry(
        LobbyEntryView entry,
        float colIdWidth,
        float titleWidth,
        float colAuthorWidth,
        float colDateWidth,
        float colViewsWidth,
        float rowContentWidth,
        float colActionsWidth)
    {
        if (_cellStyle == null || _linkStyle == null)
        {
            return;
        }

        var previousBackground = GUI.backgroundColor;
        GUI.backgroundColor = GetStatusBackground(entry.Status);

        var rowStyle = _rowBoxStyle ?? GUI.skin.box;
        var rowWidth = rowContentWidth + rowStyle.padding.left + rowStyle.padding.right;
        GUILayout.BeginVertical(rowStyle, GUILayout.Width(rowWidth), GUILayout.ExpandWidth(false));
        GUI.backgroundColor = previousBackground;

        GUILayout.BeginHorizontal(GUILayout.Width(rowContentWidth), GUILayout.ExpandWidth(false));
        GUILayout.Label(DisplayOrDash(entry.PostId), _cellStyle, GUILayout.Width(colIdWidth));
        GUILayout.Label(DisplayOrDash(entry.PostTitle), _cellStyle, GUILayout.Width(titleWidth));
        GUILayout.Label(DisplayOrDash(entry.Author), _cellStyle, GUILayout.Width(colAuthorWidth));
        var dateStyle = _dateStyle ?? _cellStyle;
        if (dateStyle != null && _cellStyle != null)
        {
            dateStyle.normal.textColor = GetDateHighlightColor(entry.PostDate, _cellStyle.normal.textColor);
        }
        GUILayout.Label(DisplayOrDash(entry.PostDate), dateStyle ?? _cellStyle, GUILayout.Width(colDateWidth));
        GUILayout.Label(DisplayOrDash(entry.Views), _cellStyle, GUILayout.Width(colViewsWidth));
        GUILayout.EndHorizontal();

        var linkWidth = Math.Max(0f, rowContentWidth - colActionsWidth);

        GUILayout.BeginHorizontal(GUILayout.Width(rowContentWidth), GUILayout.ExpandWidth(false));
        GUILayout.BeginVertical(GUILayout.Width(linkWidth), GUILayout.ExpandWidth(false));
        GUILayout.Label(entry.Link, _linkStyle, GUILayout.Width(linkWidth));
        // Info line removed; Join button now carries status/members text.
        GUILayout.EndVertical();
        GUILayout.Space(ActionGapWidth);
        var buttonHeight = ActionButtonHeight;
        if (!string.IsNullOrWhiteSpace(entry.PostUrl))
        {
            if (GUILayout.Button(
                "Open Post",
                _buttonStyle ?? GUI.skin.button,
                GUILayout.Width(OpenPostWidth),
                GUILayout.Height(buttonHeight)))
            {
                Application.OpenURL(entry.PostUrl);
            }
        }
        else
        {
            GUILayout.Space(OpenPostWidth);
        }
        GUILayout.Space(ActionButtonSpacing);
        var joinLabel = BuildJoinButtonLabel(entry);
        var joinStyle = GetJoinButtonStyle(entry);
        if (GUILayout.Button(joinLabel, joinStyle, GUILayout.Width(JoinButtonWidth), GUILayout.Height(buttonHeight)))
        {
            TryJoinLobby(entry.Link, true);
        }
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
    }

    private void ToggleUi()
    {
        _showUi = !_showUi;
        TrySetWindowBlockingInput(_showUi);
    }

    private static string DisplayOrDash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static Color GetStatusBackground(LobbyCheckStatus status)
    {
        return status switch
        {
            LobbyCheckStatus.Valid => new Color(0.86f, 0.97f, 0.90f),
            LobbyCheckStatus.Full => new Color(1.0f, 0.95f, 0.80f),
            LobbyCheckStatus.Invalid => new Color(1.0f, 0.86f, 0.86f),
            LobbyCheckStatus.Checking => new Color(0.86f, 0.93f, 1.0f),
            LobbyCheckStatus.SteamUnavailable => new Color(0.94f, 0.94f, 0.95f),
            _ => GUI.backgroundColor
        };
    }

    private void EnsureGuiStyles()
    {
        if (_headerStyle != null)
        {
            return;
        }

        _windowBackgroundTex = CreateSolidTexture(new Color(0.97f, 0.97f, 0.98f));
        _headerBackgroundTex = CreateSolidTexture(new Color(0.90f, 0.92f, 0.95f));

        _headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            clipping = TextClipping.Clip
        };

        _cellStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            clipping = TextClipping.Clip
        };

        _dateStyle = new GUIStyle(_cellStyle);

        _linkStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            wordWrap = true,
            clipping = TextClipping.Clip
        };

        _smallLabelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false
        };

        _menuLabelStyle = new GUIStyle(_smallLabelStyle);

        _headerStyle.fontSize = Math.Max(_headerStyle.fontSize, 15);
        _cellStyle.fontSize = Math.Max(_cellStyle.fontSize, 14);
        _linkStyle.fontSize = Math.Max(_linkStyle.fontSize, 13);
        _smallLabelStyle.fontSize = Math.Max(_smallLabelStyle.fontSize + 1, 12);
        _menuLabelStyle.fontSize = Math.Max(_menuLabelStyle.fontSize + 4, 16);

        _headerStyle.normal.textColor = new Color(0.10f, 0.10f, 0.12f);
        _cellStyle.normal.textColor = new Color(0.12f, 0.12f, 0.14f);
        _dateStyle.normal.textColor = _cellStyle.normal.textColor;
        _linkStyle.normal.textColor = new Color(0.10f, 0.33f, 0.74f);
        _smallLabelStyle.normal.textColor = new Color(0.18f, 0.18f, 0.20f);
        _menuLabelStyle.normal.textColor = _cellStyle.normal.textColor;

        _windowStyle = new GUIStyle(GUI.skin.window)
        {
            padding = new RectOffset(12, 12, 22, 12)
        };
        if (_windowBackgroundTex != null)
        {
            _windowStyle.normal.background = _windowBackgroundTex;
            _windowStyle.onNormal.background = _windowBackgroundTex;
        }
        var windowTextColor = new Color(0.10f, 0.10f, 0.12f);
        _windowStyle.normal.textColor = windowTextColor;
        _windowStyle.hover.textColor = windowTextColor;
        _windowStyle.active.textColor = windowTextColor;
        _windowStyle.focused.textColor = windowTextColor;
        _windowStyle.onNormal.textColor = windowTextColor;
        _windowStyle.onHover.textColor = windowTextColor;
        _windowStyle.onActive.textColor = windowTextColor;
        _windowStyle.onFocused.textColor = windowTextColor;

        _headerBoxStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(6, 6, 4, 4)
        };
        if (_headerBackgroundTex != null)
        {
            _headerBoxStyle.normal.background = _headerBackgroundTex;
            _headerBoxStyle.onNormal.background = _headerBackgroundTex;
        }

        _rowBoxStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(6, 6, 4, 4)
        };

        _buttonStyle = new GUIStyle(GUI.skin.button);
        var buttonTextColor = new Color(0.10f, 0.10f, 0.12f);
        _buttonStyle.fontSize = Math.Max(_buttonStyle.fontSize, 15);
        _buttonStyle.wordWrap = false;
        _buttonStyle.clipping = TextClipping.Clip;
        _buttonStyle.fixedHeight = ActionButtonHeight;
        _buttonStyle.stretchHeight = false;
        _buttonStyle.normal.textColor = buttonTextColor;
        _buttonStyle.hover.textColor = buttonTextColor;
        _buttonStyle.active.textColor = buttonTextColor;
        _buttonStyle.focused.textColor = buttonTextColor;
        _buttonStyle.onNormal.textColor = buttonTextColor;
        _buttonStyle.onHover.textColor = buttonTextColor;
        _buttonStyle.onActive.textColor = buttonTextColor;
        _buttonStyle.onFocused.textColor = buttonTextColor;

        _joinButtonStyle = new GUIStyle(_buttonStyle);
        _joinButtonStyle.normal.textColor = Color.white;
        _joinButtonStyle.hover.textColor = Color.white;
        _joinButtonStyle.active.textColor = Color.white;
        _joinButtonStyle.focused.textColor = Color.white;
        _joinButtonStyle.onNormal.textColor = Color.white;
        _joinButtonStyle.onHover.textColor = Color.white;
        _joinButtonStyle.onActive.textColor = Color.white;
        _joinButtonStyle.onFocused.textColor = Color.white;

        _joinButtonValidStyle = CreateJoinStateStyle(_joinButtonStyle, new Color(0.10f, 0.55f, 0.24f));
        _joinButtonFullStyle = CreateJoinStateStyle(_joinButtonStyle, new Color(0.78f, 0.42f, 0.05f));
        _joinButtonInvalidStyle = CreateJoinStateStyle(_joinButtonStyle, new Color(0.80f, 0.12f, 0.12f));

        _toggleStyle = new GUIStyle(GUI.skin.toggle);
        _toggleStyle.fontSize = Math.Max(_toggleStyle.fontSize, 15);
        _toggleStyle.normal.textColor = buttonTextColor;
        _toggleStyle.hover.textColor = buttonTextColor;
        _toggleStyle.active.textColor = buttonTextColor;
        _toggleStyle.focused.textColor = buttonTextColor;
        _toggleStyle.onNormal.textColor = buttonTextColor;
        _toggleStyle.onHover.textColor = buttonTextColor;
        _toggleStyle.onActive.textColor = buttonTextColor;
        _toggleStyle.onFocused.textColor = buttonTextColor;

        _headerStyle.margin = new RectOffset(0, 0, 0, 0);
        _cellStyle.margin = new RectOffset(0, 0, 0, 0);
        _dateStyle.margin = new RectOffset(0, 0, 0, 0);
        _linkStyle.margin = new RectOffset(0, 0, 0, 0);
        _smallLabelStyle.margin = new RectOffset(0, 0, 0, 0);
        _menuLabelStyle.margin = new RectOffset(0, 0, 0, 0);
        _headerBoxStyle.margin = new RectOffset(0, 0, 0, 0);
        _rowBoxStyle.margin = new RectOffset(0, 0, 0, 0);
        _buttonStyle.margin = new RectOffset(0, 0, 0, 0);
        _joinButtonStyle.margin = new RectOffset(0, 0, 0, 0);
        _toggleStyle.margin = new RectOffset(0, 0, 0, 0);
        if (_joinButtonValidStyle != null)
        {
            _joinButtonValidStyle.margin = _joinButtonStyle.margin;
            _joinButtonValidStyle.fixedHeight = _joinButtonStyle.fixedHeight;
            _joinButtonValidStyle.stretchHeight = _joinButtonStyle.stretchHeight;
        }
        if (_joinButtonFullStyle != null)
        {
            _joinButtonFullStyle.margin = _joinButtonStyle.margin;
            _joinButtonFullStyle.fixedHeight = _joinButtonStyle.fixedHeight;
            _joinButtonFullStyle.stretchHeight = _joinButtonStyle.stretchHeight;
        }
        if (_joinButtonInvalidStyle != null)
        {
            _joinButtonInvalidStyle.margin = _joinButtonStyle.margin;
            _joinButtonInvalidStyle.fixedHeight = _joinButtonStyle.fixedHeight;
            _joinButtonInvalidStyle.stretchHeight = _joinButtonStyle.stretchHeight;
        }
    }

    private void BindConfig()
    {
        _enabled = Config.Bind("General", "Enabled", true, "Enable MangHoMagnet.");
        _galleryListUrl = Config.Bind(
            "General",
            "GalleryListUrl",
            "https://gall.dcinside.com/mgallery/board/lists?id=bingbong",
            "DCInside gallery list URL to scan.");
        _subjectKeyword = Config.Bind(
            "Filter",
            "SubjectKeyword",
            string.Empty,
            "Ignored (no subject filtering; scans for Steam links in posts).");
        _pollIntervalSeconds = Config.Bind(
            "General",
            "PollIntervalSeconds",
            10,
            "Seconds between list scans.");
        _maxPostsPerPoll = Config.Bind(
            "General",
            "MaxPostsPerPoll",
            50,
            "Maximum number of posts to scan per poll (minimum 50 to cover the first page).");
        _maxLobbyEntries = Config.Bind(
            "General",
            "MaxLobbyEntries",
            200,
            "Maximum number of lobby links kept in memory.");
        _validateLobbies = Config.Bind(
            "General",
            "ValidateLobbies",
            true,
            "Enable lobby validation and highlight entries in the UI.");
        _validationMode = Config.Bind(
            "General",
            "ValidationMode",
            "FormatOnly",
            "Validation mode: None, FormatOnly, Steam. Steam mode can trigger in-game dialogs.");
        _suppressLobbyPopups = Config.Bind(
            "General",
            "SuppressLobbyPopups",
            true,
            "Auto-dismiss in-game lobby error dialogs while validating Steam lobbies.");
        _validationIntervalSeconds = Config.Bind(
            "General",
            "ValidationIntervalSeconds",
            60,
            "Minimum seconds between Steam lobby validation checks per link.");
        _expectedAppId = Config.Bind(
            "General",
            "ExpectedAppId",
            3527290,
            "Expected Steam App ID for lobby links.");
        _autoJoin = Config.Bind(
            "Join",
            "AutoJoin",
            false,
            "Automatically open valid Steam join links when found.");
        _joinCooldownSeconds = Config.Bind(
            "Join",
            "JoinCooldownSeconds",
            10,
            "Minimum seconds between automatic joins.");
        _userAgent = Config.Bind(
            "Network",
            "UserAgent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
            "User-Agent header used for requests.");
        _logFoundLinks = Config.Bind(
            "General",
            "LogFoundLinks",
            true,
            "Log newly discovered Steam join links.");
        _toggleKey = Config.Bind(
            "UI",
            "ToggleKey",
            new KeyboardShortcut(KeyCode.F8),
            "Toggle the lobby list window.");
        _showUiOnStart = Config.Bind(
            "UI",
            "ShowOnStart",
            false,
            "Show the lobby list window on startup.");
        _uiWidth = Config.Bind(
            "UI",
            "WindowWidth",
            1650,
            "Lobby list window width in pixels.");
        _uiHeight = Config.Bind(
            "UI",
            "WindowHeight",
            620,
            "Lobby list window height in pixels.");
    }

    private int GetRemainingSeconds()
    {
        if (_nextPollUtc == DateTime.MinValue)
        {
            return 0;
        }

        var remaining = _nextPollUtc - DateTime.UtcNow;
        return Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
    }

    private void SetNextPollUtc()
    {
        var delaySeconds = Math.Max(_pollIntervalSeconds.Value, 1);
        _nextPollUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
    }

    private float GetScrollViewWidth()
    {
        var scrollbarWidth = GUI.skin.verticalScrollbar?.fixedWidth ?? 0f;
        if (scrollbarWidth <= 0f)
        {
            scrollbarWidth = 16f;
        }
        var windowPadding = _windowStyle?.padding ?? GUI.skin.window.padding;
        var padding = windowPadding.left + windowPadding.right;
        return Mathf.Max(_windowRect.width - padding - 8f, 720f);
    }

    private float GetScrollViewContentWidth(float viewWidth)
    {
        var scrollbarWidth = GUI.skin.verticalScrollbar?.fixedWidth ?? 0f;
        if (scrollbarWidth <= 0f)
        {
            scrollbarWidth = 16f;
        }

        return Mathf.Max(viewWidth - scrollbarWidth - 2f, 300f);
    }

    private static float GetContentWidthForStyle(float width, GUIStyle style)
    {
        if (style == null)
        {
            return width;
        }

        var padding = style.padding.left + style.padding.right;
        return Mathf.Max(width - padding, 300f);
    }

    private void TryInitializeSteamValidation()
    {
        if (!_validateLobbies.Value)
        {
            _steamValidationEnabled = false;
            return;
        }

        if (GetValidationMode() != ValidationMode.Steam)
        {
            _steamValidationEnabled = false;
            return;
        }

        try
        {
            _lobbyDataCallback = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdated);
            _steamValidationEnabled = true;
        }
        catch (Exception ex)
        {
            _steamValidationEnabled = false;
            Log.LogWarning($"Steam lobby validation disabled: {ex.Message}");
        }
    }

    private void ApplyHarmonyPatches()
    {
        if (_harmony != null)
        {
            return;
        }

        try
        {
            _harmony = new Harmony("com.github.manghomagnet");
            var handlerType = AccessTools.TypeByName("SteamLobbyHandler");
            if (handlerType == null)
            {
                Log.LogWarning("Failed to find SteamLobbyHandler type for popup suppression.");
                return;
            }

            var target = AccessTools.Method(handlerType, "OnLobbyDataUpdate");
            if (target == null)
            {
                Log.LogWarning("Failed to find SteamLobbyHandler.OnLobbyDataUpdate for popup suppression.");
                return;
            }

            var prefix = new HarmonyMethod(typeof(Plugin), nameof(SteamLobbyHandler_OnLobbyDataUpdate_Prefix));
            _harmony.Patch(target, prefix: prefix);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to apply Harmony patches: {ex.Message}");
        }
    }

    private static void SteamLobbyHandler_OnLobbyDataUpdate_Prefix(object __instance, ref LobbyDataUpdate_t param)
    {
        var plugin = Instance;
        if (plugin == null)
        {
            return;
        }

        if (!plugin.ShouldSuppressLobbyPopup(__instance, param))
        {
            return;
        }

        param.m_bSuccess = 1;
    }

    private bool ShouldSuppressLobbyPopup(object handler, LobbyDataUpdate_t param)
    {
        if (!_suppressLobbyPopups.Value)
        {
            return false;
        }

        if (!_validateLobbies.Value || GetValidationMode() != ValidationMode.Steam)
        {
            return false;
        }

        if (param.m_bSuccess == 1)
        {
            return false;
        }

        if (handler == null)
        {
            return false;
        }

        var gameRequestActive = TryIsGameLobbyRequestActive(handler, param);
        return gameRequestActive == false;
    }

    private static bool? TryIsGameLobbyRequestActive(object handler, LobbyDataUpdate_t param)
    {
        try
        {
            var field = AccessTools.Field(handler.GetType(), "m_currentlyFetchingGameVersion");
            if (field == null)
            {
                return null;
            }

            var option = field.GetValue(handler);
            if (option == null)
            {
                return null;
            }

            var optionType = option.GetType();
            var isSomeProp = optionType.GetProperty("IsSome", BindingFlags.Public | BindingFlags.Instance);
            if (isSomeProp == null)
            {
                return null;
            }

            if (isSomeProp.GetValue(option) is not bool isSome)
            {
                return null;
            }

            if (!isSome)
            {
                return false;
            }

            var valueProp = optionType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (valueProp == null)
            {
                return true;
            }

            var value = valueProp.GetValue(option);
            if (value is CSteamID steamId)
            {
                return steamId.m_SteamID == param.m_ulSteamIDLobby;
            }

            var steamIdField = value?.GetType().GetField("m_SteamID", BindingFlags.Public | BindingFlags.Instance);
            if (steamIdField != null && value != null)
            {
                if (steamIdField.GetValue(value) is ulong id)
                {
                    return id == param.m_ulSteamIDLobby;
                }
            }

            return true;
        }
        catch
        {
            return null;
        }
    }

    private void TryDismissLobbyPopups()
    {
        if (!_suppressLobbyPopups.Value || !_steamValidationEnabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastModalCheckUtc).TotalSeconds < 0.5)
        {
            return;
        }

        _lastModalCheckUtc = now;

        var dismissed = false;
        dismissed |= DismissByTextType("UnityEngine.UI.Text, UnityEngine.UI");
        dismissed |= DismissByTextType("TMPro.TMP_Text, Unity.TextMeshPro");

        if (dismissed)
        {
            Log.LogDebug("Suppressed a lobby error modal.");
        }
    }

    private static bool DismissByTextType(string typeName)
    {
        var textType = Type.GetType(typeName);
        if (textType == null)
        {
            return false;
        }

        UnityEngine.Object[]? instances;
        try
        {
            instances = Resources.FindObjectsOfTypeAll(textType);
        }
        catch
        {
            return false;
        }

        if (instances == null || instances.Length == 0)
        {
            return false;
        }

        var textProperty = textType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
        if (textProperty == null)
        {
            return false;
        }

        var dismissed = false;
        foreach (var instance in instances)
        {
            if (instance is not Component component)
            {
                continue;
            }

            string? textValue = null;
            try
            {
                textValue = textProperty.GetValue(instance) as string;
            }
            catch
            {
            }

            if (!ContainsBlockedPhrase(textValue))
            {
                continue;
            }

            if (DismissModalFromComponent(component))
            {
                dismissed = true;
            }
        }

        return dismissed;
    }

    private static bool ContainsBlockedPhrase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var phrase in ModalBlockedPhrases)
        {
            if (text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool DismissModalFromComponent(Component component)
    {
        var current = component.transform;
        for (var depth = 0; depth < 6 && current != null; depth++)
        {
            if (TryClickAnyButton(current))
            {
                return true;
            }

            if (IsLikelyModalName(current.gameObject.name))
            {
                current.gameObject.SetActive(false);
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static bool TryClickAnyButton(Transform root)
    {
        var buttonType = Type.GetType("UnityEngine.UI.Button, UnityEngine.UI");
        if (buttonType == null)
        {
            return false;
        }

        Component? button = null;
        try
        {
            button = root.GetComponentInChildren(buttonType, true);
        }
        catch
        {
            return false;
        }

        if (button == null)
        {
            return false;
        }

        var onClickProp = buttonType.GetProperty("onClick", BindingFlags.Public | BindingFlags.Instance);
        var onClick = onClickProp?.GetValue(button);
        var invokeMethod = onClick?.GetType().GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
        if (invokeMethod == null)
        {
            return false;
        }

        invokeMethod.Invoke(onClick, null);
        return true;
    }

    private static bool IsLikelyModalName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.IndexOf("modal", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("popup", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("dialog", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("alert", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("notice", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("알림", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("팝업", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("메시지", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static HttpClient CreateHttpClient(string userAgent)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        client.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        return client;
    }

    private async Task<string?> FetchStringAsync(string url, CancellationToken token, string context)
    {
        if (_http == null)
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
            {
                Log.LogWarning(
                    $"Failed to fetch {context}: {url} (status {(int)response.StatusCode} {response.ReasonPhrase})");
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to fetch {context}: {url} ({ex.Message})");
            return null;
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_enabled.Value)
                {
                    await PollOnceAsync(token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Poll loop error: {ex}");
            }

            var delaySeconds = Math.Max(_pollIntervalSeconds.Value, 1);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void RequestManualPoll()
    {
        if (_cts == null || _cts.IsCancellationRequested)
        {
            return;
        }

        _ = Task.Run(() => PollOnceAsync(_cts.Token));
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        await _pollGate.WaitAsync(token);
        try
        {
            if (_http == null)
            {
                return;
            }

            var listUrl = (_galleryListUrl.Value ?? string.Empty).Trim();
            if (listUrl.Length == 0)
            {
                return;
            }

            var listHtml = await FetchStringAsync(listUrl, token, "list page");
            if (string.IsNullOrWhiteSpace(listHtml))
            {
                return;
            }

            _lastPollUtc = DateTime.UtcNow;

            var subjectKeyword = (_subjectKeyword.Value ?? string.Empty).Trim();
            var maxPosts = Math.Max(_maxPostsPerPoll.Value, 50);
            var postInfos = ExtractPostInfos(listHtml, subjectKeyword, maxPosts, out var diagnostics);
            if (postInfos.Count == 0)
            {
                if (!_loggedListEmpty)
                {
                    LogListDiagnostics(diagnostics, listUrl);
                    _loggedListEmpty = true;
                }
                return;
            }

            _loggedListEmpty = false;
            var scannedPosts = 0;
            var foundLinks = 0;

            foreach (var postInfo in postInfos)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var isNewPost = _seenPostUrls.Add(postInfo.Url);
                var needsFetch = isNewPost;
                if (!isNewPost && _postInfoByUrl.TryGetValue(postInfo.Url, out var previous))
                {
                    if (HasListMetadataChanged(previous, postInfo))
                    {
                        needsFetch = true;
                    }
                }

                _postInfoByUrl[postInfo.Url] = postInfo;

                if (!needsFetch)
                {
                    continue;
                }

                var postHtml = await FetchStringAsync(postInfo.Url, token, "post");
                if (string.IsNullOrWhiteSpace(postHtml))
                {
                    continue;
                }

                scannedPosts++;

                var fullPostDate = ExtractExactPostDate(postHtml);
                var effectivePostInfo = string.IsNullOrWhiteSpace(fullPostDate)
                    ? postInfo
                    : postInfo.WithDate(fullPostDate);

                foreach (var link in ExtractSteamLinks(postHtml))
                {
                    if (_logFoundLinks.Value)
                    {
                        Log.LogInfo($"Found lobby link: {link} (post: {effectivePostInfo.Url})");
                    }

                    AddLobbyEntry(link, effectivePostInfo);
                    TryJoinLobby(link, false);
                    foundLinks++;
                }
            }

            if (foundLinks == 0)
            {
                if (!_loggedNoLinks)
                {
                    Log.LogWarning(
                        $"No lobby links found this poll. ScannedPosts={scannedPosts}, Rows={diagnostics.RowCount}, SubjectMatches={diagnostics.SubjectMatchCount}, Title='{diagnostics.Title}'.");
                    _loggedNoLinks = true;
                }
            }
            else
            {
                _loggedNoLinks = false;
            }
        }
        finally
        {
            SetNextPollUtc();
            _pollGate.Release();
        }
    }

    private List<PostInfo> ExtractPostInfos(
        string html,
        string subjectKeyword,
        int maxPosts,
        out ListParseDiagnostics diagnostics)
    {
        diagnostics = new ListParseDiagnostics();
        if (string.IsNullOrWhiteSpace(html))
        {
            return new List<PostInfo>();
        }

        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        diagnostics.Title = document.Title ?? string.Empty;

        var rows = document.QuerySelectorAll("tr.ub-content");
        diagnostics.RowCount = rows.Length;

        var results = new List<PostInfo>();
        foreach (var row in rows)
        {
            if (maxPosts <= 0)
            {
                break;
            }

            var postInfo = TryExtractPostInfo(row, subjectKeyword, diagnostics);
            if (postInfo == null)
            {
                continue;
            }

            results.Add(postInfo);
            maxPosts--;
        }

        diagnostics.PostInfoCount = results.Count;
        return results;
    }

    private static PostInfo? TryExtractPostInfo(
        IElement row,
        string subjectKeyword,
        ListParseDiagnostics diagnostics)
    {
        if (!SubjectMatches(row, subjectKeyword))
        {
            return null;
        }

        diagnostics.SubjectMatchCount++;

        var titleAnchor = row.QuerySelector("td.gall_tit a");
        if (titleAnchor == null)
        {
            diagnostics.MissingTitleCount++;
            return null;
        }

        var href = titleAnchor.GetAttribute("href");
        if (string.IsNullOrWhiteSpace(href))
        {
            diagnostics.MissingHrefCount++;
            return null;
        }

        if (!href.Contains("board/view", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var title = CleanText(titleAnchor.TextContent);
        var replyCount = ExtractReplyCount(row);
        if (replyCount > 0 && !title.Contains($"[{replyCount}]", StringComparison.Ordinal))
        {
            title = $"{title} [{replyCount}]";
        }

        var postId = ExtractPostId(row);
        var author = ExtractAuthor(row);
        var postDate = ExtractPostDate(row);
        var views = ExtractViewCount(row);
        var url = NormalizeUrl(WebUtility.HtmlDecode(href));

        return new PostInfo(postId, title, author, postDate, views, url);
    }

    private static bool SubjectMatches(IElement row, string subjectKeyword)
    {
        return true;
    }

    private static string ExtractPostId(IElement row)
    {
        var id = row.GetAttribute("data-no");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = CleanText(row.QuerySelector("td.gall_num")?.TextContent ?? string.Empty);
        }

        return id ?? string.Empty;
    }

    private static string ExtractAuthor(IElement row)
    {
        return CleanText(row.QuerySelector("td.gall_writer")?.TextContent ?? string.Empty);
    }

    private static string ExtractPostDate(IElement row)
    {
        var dateCell = row.QuerySelector("td.gall_date");
        if (dateCell == null)
        {
            return string.Empty;
        }

        var full = dateCell.GetAttribute("title") ?? string.Empty;
        var shortText = dateCell.TextContent ?? string.Empty;
        return FormatDate(full, shortText);
    }

    private static string ExtractExactPostDate(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        foreach (var element in document.QuerySelectorAll(".gall_date"))
        {
            var candidate = element.GetAttribute("title");
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = element.TextContent;
            }

            var normalizedCandidate = CleanText(candidate ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedCandidate))
            {
                continue;
            }

            if (TryParseExactDate(normalizedCandidate, out var parsed))
            {
                return parsed.ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            var normalized = NormalizeExactDateText(normalizedCandidate);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return string.Empty;
    }

    private static string ExtractViewCount(IElement row)
    {
        var cleaned = CleanText(row.QuerySelector("td.gall_count")?.TextContent ?? string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return cleaned;
        }

        var digitsOnly = Regex.Replace(cleaned, @"\D", string.Empty);
        return string.IsNullOrWhiteSpace(digitsOnly) ? cleaned : digitsOnly;
    }

    private static int ExtractReplyCount(IElement row)
    {
        return ParseIntFromText(row.QuerySelector("span.reply_num")?.TextContent);
    }

    private static int ParseIntFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var match = Regex.Match(text, @"\d+");
        return match.Success && int.TryParse(match.Value, out var value) ? value : 0;
    }

    private static string FormatDate(string full, string shortText)
    {
        var trimmedFull = (full ?? string.Empty).Trim();
        if (DateTime.TryParseExact(
                trimmedFull,
                new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed) ||
            DateTime.TryParse(trimmedFull, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed) ||
            DateTime.TryParse(trimmedFull, out parsed))
        {
            return parsed.ToString("MM-dd HH:mm");
        }

        return CleanText(shortText);
    }

    private static bool TryParseExactDate(string value, out DateTime parsed)
    {
        return DateTime.TryParseExact(
                   value,
                   new[]
                   {
                       "yyyy-MM-dd HH:mm:ss",
                       "yyyy-MM-dd HH:mm",
                       "yyyy.MM.dd HH:mm:ss",
                       "yyyy.MM.dd HH:mm",
                       "yyyy/MM/dd HH:mm:ss",
                       "yyyy/MM/dd HH:mm"
                   },
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces,
                   out parsed) ||
               DateTime.TryParse(
                   value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces,
                   out parsed);
    }

    private static string NormalizeExactDateText(string value)
    {
        var match = Regex.Match(
            value,
            @"(?<date>\d{4}[./-]\d{2}[./-]\d{2})\s+(?<time>\d{2}:\d{2}(?::\d{2})?)");
        if (!match.Success)
        {
            return value;
        }

        var datePart = match.Groups["date"].Value
            .Replace('-', '.')
            .Replace('/', '.');
        var timePart = match.Groups["time"].Value;
        if (timePart.Length == 5)
        {
            timePart += ":00";
        }

        return $"{datePart} {timePart}";
    }

    private static bool HasListMetadataChanged(PostInfo previous, PostInfo current)
    {
        if (!string.Equals(previous.Title, current.Title, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(previous.Author, current.Author, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(previous.Date, current.Date, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(previous.Views, current.Views, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private ValidationMode GetValidationMode()
    {
        var modeRaw = (_validationMode.Value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(modeRaw))
        {
            return ValidationMode.FormatOnly;
        }

        if (Enum.TryParse(modeRaw, true, out ValidationMode mode))
        {
            return mode;
        }

        return ValidationMode.FormatOnly;
    }

    private static string NormalizeUrl(string href)
    {
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return href;
        }

        if (href.StartsWith("//", StringComparison.OrdinalIgnoreCase))
        {
            return $"https:{href}";
        }

        if (href.StartsWith("/", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://gall.dcinside.com{href}";
        }

        return $"https://gall.dcinside.com/{href}";
    }

    private static string CleanText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var noTags = TagRegex.Replace(html, string.Empty);
        return WebUtility.HtmlDecode(noTags).Trim();
    }

    private static Color GetDateHighlightColor(string? dateText, Color baseColor)
    {
        if (string.IsNullOrWhiteSpace(dateText))
        {
            return baseColor;
        }

        if (!TryParseExactDate(dateText, out var parsed))
        {
            return baseColor;
        }

        var delta = DateTime.Now - parsed;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta <= TimeSpan.FromMinutes(10))
        {
            return new Color(0.12f, 0.7f, 0.18f);
        }

        if (delta <= TimeSpan.FromHours(1))
        {
            return new Color(0.22f, 0.62f, 0.2f);
        }

        if (delta <= TimeSpan.FromHours(3))
        {
            return new Color(0.92f, 0.78f, 0.22f);
        }

        if (delta <= TimeSpan.FromHours(12))
        {
            return new Color(0.95f, 0.55f, 0.15f);
        }

        if (delta <= TimeSpan.FromHours(24))
        {
            return new Color(0.6f, 0.6f, 0.6f);
        }

        return baseColor;
    }

    private string BuildLinkInfoText(LobbyEntryView entry)
    {
        var statusText = GetStatusLabel(entry);
        var membersText = GetMembersLabel(entry);
        if (string.IsNullOrWhiteSpace(membersText))
        {
            return statusText;
        }

        return $"{statusText} | {membersText}";
    }

    private string BuildJoinButtonLabel(LobbyEntryView entry)
    {
        var hasMembers = entry.MemberCount >= 0 && entry.MemberLimit > 0;
        var isFull = hasMembers && entry.MemberCount >= entry.MemberLimit;

        if (isFull)
        {
            return $"Join (Full | Members {entry.MemberCount}/{entry.MemberLimit})";
        }

        return entry.Status switch
        {
            LobbyCheckStatus.Invalid => "Join (Invalid)",
            LobbyCheckStatus.Full => hasMembers ? $"Join (Full | Members {entry.MemberCount}/{entry.MemberLimit})" : "Join (Full)",
            LobbyCheckStatus.Valid => hasMembers ? $"Join (Members {entry.MemberCount}/{entry.MemberLimit})" : "Join (Valid)",
            LobbyCheckStatus.Checking => "Join (Checking)",
            LobbyCheckStatus.SteamUnavailable => "Join (Steam offline)",
            _ => "Join"
        };
    }

    private GUIStyle GetJoinButtonStyle(LobbyEntryView entry)
    {
        var baseStyle = _joinButtonStyle ?? _buttonStyle ?? GUI.skin.button;
        var hasMembers = entry.MemberCount >= 0 && entry.MemberLimit > 0;
        var isFull = hasMembers && entry.MemberCount >= entry.MemberLimit;

        if (entry.Status == LobbyCheckStatus.Invalid)
        {
            return _joinButtonInvalidStyle ?? baseStyle;
        }

        if (isFull || entry.Status == LobbyCheckStatus.Full)
        {
            return _joinButtonFullStyle ?? baseStyle;
        }

        if (entry.Status == LobbyCheckStatus.Valid)
        {
            return _joinButtonValidStyle ?? baseStyle;
        }

        return baseStyle;
    }

    private static GUIStyle CreateJoinStateStyle(GUIStyle baseStyle, Color color)
    {
        var style = new GUIStyle(baseStyle);
        style.normal.textColor = color;
        style.hover.textColor = color;
        style.active.textColor = color;
        style.focused.textColor = color;
        style.onNormal.textColor = color;
        style.onHover.textColor = color;
        style.onActive.textColor = color;
        style.onFocused.textColor = color;
        return style;
    }

    private string GetStatusLabel(LobbyEntryView entry)
    {
        return entry.Status switch
        {
            LobbyCheckStatus.Unknown => "Not checked",
            LobbyCheckStatus.Checking => "Checking",
            LobbyCheckStatus.Valid => GetValidationMode() == ValidationMode.FormatOnly ? "Valid (format)" : "Valid",
            LobbyCheckStatus.Full => "Full",
            LobbyCheckStatus.Invalid => "Invalid",
            LobbyCheckStatus.SteamUnavailable => "Steam unavailable",
            _ => "Unknown"
        };
    }

    private static string GetMembersLabel(LobbyEntryView entry)
    {
        if (entry.MemberLimit > 0 && entry.MemberCount >= 0)
        {
            return $"Members {entry.MemberCount}/{entry.MemberLimit}";
        }

        if (entry.MemberCount >= 0)
        {
            return $"Members {entry.MemberCount}";
        }

        return string.Empty;
    }

    private static Texture2D CreateSolidTexture(Color color)
    {
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, color);
        tex.Apply();
        return tex;
    }

    private void LogListDiagnostics(ListParseDiagnostics diagnostics, string listUrl)
    {
        Log.LogWarning(
            $"List parse returned 0 posts. Rows={diagnostics.RowCount}, SubjectMatches={diagnostics.SubjectMatchCount}, MissingTitle={diagnostics.MissingTitleCount}, MissingHref={diagnostics.MissingHrefCount}, Title='{diagnostics.Title}', Url={listUrl}");
    }

    private IEnumerable<string> ExtractSteamLinks(string html)
    {
        foreach (Match match in SteamLinkRegex.Matches(html))
        {
            yield return match.Value;
        }
    }

    private static bool TryParseLobbyLink(string link, out uint appId, out ulong lobbyId, out ulong hostId)
    {
        appId = 0;
        lobbyId = 0;
        hostId = 0;

        var match = SteamLinkParseRegex.Match(link);
        if (!match.Success)
        {
            return false;
        }

        return uint.TryParse(match.Groups["app"].Value, out appId)
            && ulong.TryParse(match.Groups["lobby"].Value, out lobbyId)
            && ulong.TryParse(match.Groups["host"].Value, out hostId);
    }

    private void OnLobbyDataUpdated(LobbyDataUpdate_t callback)
    {
        var lobbyId = callback.m_ulSteamIDLobby;
        LobbyEntry? entry;

        lock (_lobbyLock)
        {
            if (!_lobbyById.TryGetValue(lobbyId, out entry))
            {
                return;
            }

            entry.IsCheckPending = false;
        }

        if (callback.m_bSuccess == 0)
        {
            lock (_lobbyLock)
            {
                entry.Status = LobbyCheckStatus.Invalid;
                entry.MemberCount = -1;
                entry.MemberLimit = -1;
            }
        }
        else
        {
            try
            {
                var steamId = new CSteamID(lobbyId);
                var memberCount = SteamMatchmaking.GetNumLobbyMembers(steamId);
                var memberLimit = SteamMatchmaking.GetLobbyMemberLimit(steamId);
                var status = LobbyCheckStatus.Valid;
                if (memberLimit > 0 && memberCount >= memberLimit)
                {
                    status = LobbyCheckStatus.Full;
                }

                lock (_lobbyLock)
                {
                    entry.Status = status;
                    entry.MemberCount = memberCount;
                    entry.MemberLimit = memberLimit;
                }

                if (status == LobbyCheckStatus.Valid)
                {
                    TryJoinLobby(entry.Link, false);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Steam lobby validation error: {ex.Message}");
                lock (_lobbyLock)
                {
                    entry.Status = LobbyCheckStatus.SteamUnavailable;
                    entry.MemberCount = -1;
                    entry.MemberLimit = -1;
                }
            }
        }

        Interlocked.Increment(ref _lobbyVersion);
    }

    private void ProcessLobbyChecks()
    {
        var processed = 0;
        while (processed < MaxLobbyChecksPerFrame)
        {
            ulong lobbyId;
            lock (_pendingLobbyLock)
            {
                if (_pendingLobbyChecks.Count == 0)
                {
                    break;
                }

                lobbyId = _pendingLobbyChecks.Dequeue();
            }

            bool requested = false;
            try
            {
                requested = SteamMatchmaking.RequestLobbyData(new CSteamID(lobbyId));
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Steam lobby validation failed: {ex.Message}");
            }

            if (!requested)
            {
                lock (_lobbyLock)
                {
                    if (_lobbyById.TryGetValue(lobbyId, out var entry))
                    {
                        entry.IsCheckPending = false;
                        entry.Status = LobbyCheckStatus.SteamUnavailable;
                    }
                }

                Interlocked.Increment(ref _lobbyVersion);
            }

            processed++;
        }
    }

    private void AddLobbyEntry(string link, PostInfo postInfo)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        LobbyEntry? entry;
        bool shouldValidate = false;
        ulong lobbyIdForCheck = 0;

        lock (_lobbyLock)
        {
            if (!_lobbyByLink.TryGetValue(link, out entry))
            {
                entry = new LobbyEntry(link);
                _lobbyByLink[link] = entry;
                _lobbyEntries.Add(entry);
            }

            entry.AddOrUpdateSource(postInfo);
            entry.Touch();
            TrimLobbyEntries();

            if (!entry.HasLobbyId)
            {
                if (TryParseLobbyLink(link, out var appId, out var lobbyId, out var hostId))
                {
                    entry.SetLobbyInfo(appId, lobbyId, hostId);
                    _lobbyById[lobbyId] = entry;
                }
                else
                {
                    entry.Status = LobbyCheckStatus.Invalid;
                }
            }

            if (entry.HasLobbyId)
            {
                var expectedAppId = (uint)Math.Max(_expectedAppId.Value, 0);
                if (expectedAppId != 0 && entry.AppId != expectedAppId)
                {
                    entry.Status = LobbyCheckStatus.Invalid;
                    entry.MemberCount = -1;
                    entry.MemberLimit = -1;
                }
                else
                {
                    var mode = GetValidationMode();
                    if (!_validateLobbies.Value || mode == ValidationMode.None)
                    {
                        entry.Status = LobbyCheckStatus.Unknown;
                        entry.MemberCount = -1;
                        entry.MemberLimit = -1;
                    }
                    else if (mode == ValidationMode.FormatOnly)
                    {
                        entry.Status = LobbyCheckStatus.Valid;
                        entry.MemberCount = -1;
                        entry.MemberLimit = -1;
                    }
                    else if (_steamValidationEnabled && !entry.IsCheckPending)
                    {
                        var intervalSeconds = Math.Max(_validationIntervalSeconds.Value, 5);
                        if (entry.LastCheckUtc == DateTime.MinValue ||
                            DateTime.UtcNow - entry.LastCheckUtc >= TimeSpan.FromSeconds(intervalSeconds))
                        {
                            entry.LastCheckUtc = DateTime.UtcNow;
                            entry.IsCheckPending = true;
                            entry.Status = LobbyCheckStatus.Checking;
                            entry.MemberCount = -1;
                            entry.MemberLimit = -1;
                            shouldValidate = true;
                            lobbyIdForCheck = entry.LobbyId;
                        }
                    }
                    else if (_steamValidationEnabled)
                    {
                        entry.Status = LobbyCheckStatus.Checking;
                        entry.MemberCount = -1;
                        entry.MemberLimit = -1;
                    }
                    else
                    {
                        entry.Status = LobbyCheckStatus.SteamUnavailable;
                        entry.MemberCount = -1;
                        entry.MemberLimit = -1;
                    }
                }
            }
        }

        if (shouldValidate && lobbyIdForCheck != 0)
        {
            lock (_pendingLobbyLock)
            {
                _pendingLobbyChecks.Enqueue(lobbyIdForCheck);
            }
        }

        Interlocked.Increment(ref _lobbyVersion);
    }

    private void TrimLobbyEntries()
    {
        var maxEntries = Math.Max(_maxLobbyEntries.Value, 10);
        while (_lobbyEntries.Count > maxEntries)
        {
            var oldest = _lobbyEntries.OrderBy(e => e.FirstSeenUtc).First();
            _lobbyEntries.Remove(oldest);
            _lobbyByLink.Remove(oldest.Link);
        }
    }

    private List<LobbyEntryView> GetLobbySnapshot()
    {
        lock (_lobbyLock)
        {
            var snapshots = _lobbyEntries
                .Select(entry =>
                {
                    var source = entry.Sources
                        .OrderByDescending(item => item.AddedUtc)
                        .FirstOrDefault();
                    var sortPostId = entry.Sources
                        .Select(item => ParsePostId(item.Id))
                        .DefaultIfEmpty(0)
                        .Max();

                    return new
                    {
                        Entry = entry,
                        Source = source,
                        SortPostId = sortPostId
                    };
                })
                .GroupBy(
                    item => string.IsNullOrWhiteSpace(item.Source?.Id) ? item.Entry.Link : item.Source.Id,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => GetStatusSortRank(item.Entry.Status))
                    .ThenByDescending(item => item.Entry.LastSeenUtc)
                    .First())
                .OrderByDescending(item => item.SortPostId)
                .ThenByDescending(item => item.Entry.LastSeenUtc)
                .Select(item => new LobbyEntryView
                {
                    Link = item.Entry.Link,
                    SourceCount = item.Entry.Sources.Count,
                    LastSeenUtc = item.Entry.LastSeenUtc,
                    PostUrl = item.Source?.Url ?? string.Empty,
                    PostId = item.Source?.Id ?? string.Empty,
                    PostTitle = item.Source?.Title ?? string.Empty,
                    Author = item.Source?.Author ?? string.Empty,
                    PostDate = item.Source?.Date ?? string.Empty,
                    Views = item.Source?.Views ?? string.Empty,
                    MemberCount = item.Entry.MemberCount,
                    MemberLimit = item.Entry.MemberLimit,
                    Status = item.Entry.Status
                })
                .ToList();

            return snapshots;
        }
    }

    private static int GetStatusSortRank(LobbyCheckStatus status)
    {
        return status switch
        {
            LobbyCheckStatus.Valid => 5,
            LobbyCheckStatus.Full => 4,
            LobbyCheckStatus.Checking => 3,
            LobbyCheckStatus.Unknown => 2,
            LobbyCheckStatus.SteamUnavailable => 1,
            LobbyCheckStatus.Invalid => 0,
            _ => 0
        };
    }

    private static long ParsePostId(string id)
    {
        if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return 0;
    }

    private void TryJoinLobby(string link, bool force)
    {
        if (!force && !_autoJoin.Value)
        {
            return;
        }

        var cooldownSeconds = Math.Max(_joinCooldownSeconds.Value, 0);
        if (!force && cooldownSeconds > 0 &&
            DateTime.UtcNow - _lastJoinUtc < TimeSpan.FromSeconds(cooldownSeconds))
        {
            return;
        }

        if (!force)
        {
            if (!TryMarkAutoJoin(link))
            {
                return;
            }
        }
        else
        {
            MarkAutoJoinAttempted(link);
        }

        try
        {
            var startInfo = new ProcessStartInfo(link)
            {
                UseShellExecute = true
            };
            Process.Start(startInfo);
            _lastJoinUtc = DateTime.UtcNow;
            Log.LogInfo($"Joining lobby: {link}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to open lobby link: {ex.Message}");
        }
    }

    private bool TryMarkAutoJoin(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return false;
        }

        lock (_lobbyLock)
        {
            if (!_lobbyByLink.TryGetValue(link, out var entry))
            {
                return false;
            }

            if (entry.Status != LobbyCheckStatus.Valid)
            {
                return false;
            }

            if (entry.AutoJoinAttempted)
            {
                return false;
            }

            entry.AutoJoinAttempted = true;
            return true;
        }
    }

    private void MarkAutoJoinAttempted(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        lock (_lobbyLock)
        {
            if (_lobbyByLink.TryGetValue(link, out var entry))
            {
                entry.AutoJoinAttempted = true;
            }
        }
    }

    private void TrySetWindowBlockingInput(bool block)
    {
        try
        {
            var type = Type.GetType("GUIManager, Assembly-CSharp");
            if (type == null)
            {
                return;
            }

            var instanceProp = type.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
            var instance = instanceProp?.GetValue(null);
            if (instance == null)
            {
                return;
            }

            object? currentValue = null;
            var field = type.GetField("windowBlockingInput", BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                currentValue = field.GetValue(instance);
            }
            else
            {
                var prop = type.GetProperty("windowBlockingInput", BindingFlags.Public | BindingFlags.Instance);
                currentValue = prop?.GetValue(instance);
            }

            var currentlyBlocked = currentValue is bool b && b;
            if (block)
            {
                if (!currentlyBlocked)
                {
                    SetWindowBlocking(instance, type, true);
                    _inputBlockedByUs = true;
                }
            }
            else if (_inputBlockedByUs)
            {
                SetWindowBlocking(instance, type, false);
                _inputBlockedByUs = false;
            }
        }
        catch
        {
        }
    }

    private static void SetWindowBlocking(object instance, Type type, bool value)
    {
        var field = type.GetField("windowBlockingInput", BindingFlags.Public | BindingFlags.Instance);
        if (field != null)
        {
            field.SetValue(instance, value);
            return;
        }

        var prop = type.GetProperty("windowBlockingInput", BindingFlags.Public | BindingFlags.Instance);
        prop?.SetValue(instance, value);
    }

    private enum LobbyCheckStatus
    {
        Unknown,
        Checking,
        Valid,
        Full,
        Invalid,
        SteamUnavailable
    }

    private enum ValidationMode
    {
        None,
        FormatOnly,
        Steam
    }

    private sealed class PostInfo
    {
        public PostInfo(string id, string title, string author, string date, string views, string url)
        {
            Id = id;
            Url = url;
            Title = title;
            Author = author;
            Date = date;
            Views = views;
        }

        public string Id { get; }
        public string Url { get; }
        public string Title { get; }
        public string Author { get; }
        public string Date { get; }
        public string Views { get; }

        public PostInfo WithDate(string date)
        {
            if (string.IsNullOrWhiteSpace(date) || date == Date)
            {
                return this;
            }

            return new PostInfo(Id, Title, Author, date, Views, Url);
        }
    }

    private sealed class PostSource
    {
        public PostSource(PostInfo postInfo)
        {
            Id = postInfo.Id;
            Title = postInfo.Title;
            Author = postInfo.Author;
            Date = postInfo.Date;
            Views = postInfo.Views;
            Url = postInfo.Url;
            AddedUtc = DateTime.UtcNow;
        }

        public string Id { get; private set; }
        public string Title { get; private set; }
        public string Author { get; private set; }
        public string Date { get; private set; }
        public string Views { get; private set; }
        public string Url { get; private set; }
        public DateTime AddedUtc { get; private set; }

        public void UpdateFrom(PostInfo postInfo)
        {
            Id = postInfo.Id;
            Title = postInfo.Title;
            Author = postInfo.Author;
            Date = postInfo.Date;
            Views = postInfo.Views;
            Url = postInfo.Url;
            AddedUtc = DateTime.UtcNow;
        }
    }

    private sealed class LobbyEntry
    {
        public LobbyEntry(string link)
        {
            Link = link;
            FirstSeenUtc = DateTime.UtcNow;
            LastSeenUtc = FirstSeenUtc;
            Status = LobbyCheckStatus.Unknown;
        }

        public string Link { get; }
        public uint AppId { get; private set; }
        public ulong LobbyId { get; private set; }
        public ulong HostId { get; private set; }
        public DateTime FirstSeenUtc { get; }
        public DateTime LastSeenUtc { get; private set; }
        public DateTime LastCheckUtc { get; set; } = DateTime.MinValue;
        public bool IsCheckPending { get; set; }
        public LobbyCheckStatus Status { get; set; }
        public bool AutoJoinAttempted { get; set; }
        public int MemberCount { get; set; } = -1;
        public int MemberLimit { get; set; } = -1;
        public List<PostSource> Sources { get; } = new List<PostSource>();
        public bool HasLobbyId => LobbyId != 0;

        public void Touch()
        {
            LastSeenUtc = DateTime.UtcNow;
        }

        public void SetLobbyInfo(uint appId, ulong lobbyId, ulong hostId)
        {
            AppId = appId;
            LobbyId = lobbyId;
            HostId = hostId;
        }

        public void AddOrUpdateSource(PostInfo postInfo)
        {
            var existing = Sources.FirstOrDefault(
                source => source.Url.Equals(postInfo.Url, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.UpdateFrom(postInfo);
                return;
            }

            Sources.Add(new PostSource(postInfo));
        }
    }

    private sealed class LobbyEntryView
    {
        public string Link { get; set; } = string.Empty;
        public string PostId { get; set; } = string.Empty;
        public string PostTitle { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string PostDate { get; set; } = string.Empty;
        public string Views { get; set; } = string.Empty;
        public string PostUrl { get; set; } = string.Empty;
        public int MemberCount { get; set; } = -1;
        public int MemberLimit { get; set; } = -1;
        public int SourceCount { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public LobbyCheckStatus Status { get; set; }
    }

    private sealed class ListParseDiagnostics
    {
        public string Title { get; set; } = string.Empty;
        public int RowCount { get; set; }
        public int SubjectMatchCount { get; set; }
        public int MissingTitleCount { get; set; }
        public int MissingHrefCount { get; set; }
        public int PostInfoCount { get; set; }
    }
}
