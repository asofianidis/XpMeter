using System.Collections.Generic;
using System.Globalization;
using FishNet;
using FishNet.Object;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NevarisXpMeter;

// Owns session state, conservative monster attribution, and the Unity overlay.
// All entry points are gated on a spawned, locally owned character.
internal static class MeterController
{
    private const float PanelWidth = 430f;
    private const float PanelHeight = 282f;
    private const float MinimumUserScale = 0.55f;
    private const float MaximumUserScale = 1.25f;
    private const float MinimumAutoFitScale = 0.35f;
    private const float SourceNameMaxWidth = 132f;
    private const string UnattributedKey = "\u0000unattributed";

    private static readonly Dictionary<string, SourceRow> RowsByName =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SourceRow> RowsByType =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<SourceCandidate> RecentDeaths = new();
    private static readonly List<PendingGain> PendingGains = new();

    private static GameObject _canvasObject;
    private static GameObject _panelObject;
    private static Canvas _canvas;
    private static RectTransform _panelRect;
    private static RectTransform _dragHandleRect;
    private static RectTransform _resizeHandleRect;
    private static Image _background;
    private static TextMeshProUGUI _titleText;
    private static TextMeshProUGUI _dragHintText;
    private static TextMeshProUGUI _text;
    private static PlayerController _localPlayer;

    private static bool _visible;
    private static bool _worldReady;
    private static bool _dragging;
    private static bool _resizing;
    private static bool _gameStyleApplied;
    private static bool _hasBaseline;
    private static bool _loggedFirstGain;
    private static bool _loggedPollingFallback;
    private static bool _loggedFirstAttribution;
    private static bool _loggedUnattributed;
    private static Vector2 _dragStartMouse;
    private static Vector2 _dragStartPosition;
    private static Vector2 _resizeStartMouse;
    private static float _resizeStartScale;
    private static float _preferredUiScale = 1f;
    private static int _lastScreenWidth = -1;
    private static int _lastScreenHeight = -1;
    private static int _lastTickFrame = -1;
    private static int _lastBaseExp;
    private static int _lastJobExp;
    private static long _totalBaseXp;
    private static long _totalJobXp;
    private static long _attributedKills;
    private static double _startedAt = -1d;
    private static double _lastGainAt = -1d;
    private static float _nextUiRefresh;
    private static string _characterId = string.Empty;

    internal static void Initialize() => _visible = Plugin.Settings.StartVisible.Value;

    internal static void ResetSession()
    {
        ClearSession();
        CaptureBaseline();
    }

    internal static void ApplyConfiguredScale()
    {
        _preferredUiScale = Mathf.Clamp(
            Plugin.Settings.UiScale.Value, MinimumUserScale, MaximumUserScale);
        if (_panelRect is null)
            return;
        ApplyScaleForScreen();
        ClampPanelToScreen();
    }

    internal static void ResetLayout()
    {
        Plugin.Settings.PositionX.Value = (float)Plugin.Settings.PositionX.DefaultValue;
        Plugin.Settings.PositionY.Value = (float)Plugin.Settings.PositionY.DefaultValue;
        Plugin.Settings.UiScale.Value = (float)Plugin.Settings.UiScale.DefaultValue;
        ApplyConfiguredScale();
        if (_panelRect is null)
            return;
        _panelRect.anchoredPosition = new Vector2(
            Plugin.Settings.PositionX.Value, Plugin.Settings.PositionY.Value);
        ClampPanelToScreen();
    }

    internal static void Shutdown()
    {
        _worldReady = false;
        DestroyUi();
        _localPlayer = null;
        _characterId = string.Empty;
        _hasBaseline = false;
        ClearSession();
    }

    internal static void EnterWorld(PlayerController localPlayer)
    {
        if (_worldReady)
            return;

        _localPlayer = localPlayer;
        _worldReady = true;
        string nextCharacterId = SafeCharacterId(localPlayer);
        bool characterChanged = !string.IsNullOrEmpty(_characterId)
            && !string.Equals(_characterId, nextCharacterId, StringComparison.Ordinal);
        if (characterChanged || Plugin.Settings.ResetOnWorldEntry.Value)
            ClearSession();
        _characterId = nextCharacterId;
        CaptureBaseline();
        EnsureUi();
        _panelObject?.SetActive(_visible && !IsMenuOpen());
    }

    internal static void LeaveWorld()
    {
        if (!_worldReady && _canvasObject is null)
            return;

        _worldReady = false;
        DestroyUi();
        _localPlayer = null;
        _hasBaseline = false;
        RecentDeaths.Clear();
        PendingGains.Clear();
    }

    internal static void Tick()
    {
        if (!_worldReady || _localPlayer is null)
            return;
        if (_lastTickFrame == Time.frameCount)
            return;
        _lastTickFrame = Time.frameCount;

        PollExperienceTotals();
        EnsureUi();
        bool menuOpen = IsMenuOpen();
        if (!menuOpen && Plugin.Settings.ToggleHotkey.Value.IsDown())
            _visible = !_visible;
        if (!menuOpen && Plugin.Settings.ResetHotkey.Value.IsDown())
            ResetSession();

        bool shouldDisplay = _visible && !menuOpen;
        if (_panelObject is not null && _panelObject.activeSelf != shouldDisplay)
            _panelObject.SetActive(shouldDisplay);

        if (shouldDisplay)
            HandlePointerInteractions();
        else
        {
            _dragging = false;
            _resizing = false;
        }

        if (!_gameStyleApplied)
            _gameStyleApplied = TryApplyGameStyle();
        if (_lastScreenWidth != Screen.width || _lastScreenHeight != Screen.height)
            ApplyScaleForScreen();
        ClampPanelToScreen();

        double now = Time.realtimeSinceStartupAsDouble;
        PruneCorrelation(now);
        if (_text is not null && Time.unscaledTime >= _nextUiRefresh)
        {
            _nextUiRefresh = Time.unscaledTime + 0.2f;
            _text.text = BuildDisplayText(now);
        }
    }

    internal static void RecordTotals(int baseExp, int baseLevel, int jobExp, int jobLevel)
    {
        if (!_worldReady || _localPlayer is null)
            return;

        if (!_hasBaseline)
        {
            _lastBaseExp = baseExp;
            _lastJobExp = jobExp;
            _hasBaseline = true;
            return;
        }

        long baseGain = (long)baseExp - _lastBaseExp;
        long jobGain = (long)jobExp - _lastJobExp;
        _lastBaseExp = baseExp;
        _lastJobExp = jobExp;

        // A respec, rollback, or character reload can lower a total. Rebaseline
        // that track without inventing a negative XP award.
        baseGain = Math.Max(0L, baseGain);
        jobGain = Math.Max(0L, jobGain);
        if (baseGain == 0L && jobGain == 0L)
            return;

        double now = Time.realtimeSinceStartupAsDouble;
        float idleReset = Plugin.Settings.NewSessionAfter.Value;
        if (idleReset > 0f && _lastGainAt >= 0d && now - _lastGainAt >= idleReset)
            ClearSession(preserveRecentDeaths: true);

        if (_startedAt < 0d)
            _startedAt = now;
        _lastGainAt = now;
        _totalBaseXp += baseGain;
        _totalJobXp += jobGain;

        PruneCorrelation(now);
        SourceCandidate source = FindBestDeath(now);
        if (source is not null)
        {
            source.Consumed = true;
            AddAttributedGain(source, baseGain, jobGain, now);
        }
        else
        {
            AddGain(RowsByName, UnattributedKey, "Other", baseGain, jobGain, 0, now);
            AddGain(RowsByType, UnattributedKey, "Other", baseGain, jobGain, 0, now);
            PendingGains.Add(new PendingGain(baseGain, jobGain, now));
            if (!_loggedUnattributed)
            {
                _loggedUnattributed = true;
                Plugin.Info("An XP award was retained under Other because no nearby relevant monster death was available.");
            }
        }

        if (!_loggedFirstGain)
        {
            _loggedFirstGain = true;
            Plugin.Info("First authoritative base/job XP update captured.");
        }
    }

    private static void PollExperienceTotals()
    {
        try
        {
            CharacterData data = _localPlayer?.Save?.Data;
            if (data is null)
                return;

            if (!_loggedPollingFallback)
            {
                _loggedPollingFallback = true;
                Plugin.Info("Local synchronized character XP polling is active as the capture fallback.");
            }

            RecordTotals(data.Exp, data.Level, data.JobExp, data.JobLevel);
        }
        catch (Exception ex)
        {
            if (_loggedPollingFallback)
                return;
            _loggedPollingFallback = true;
            Plugin.Warning($"Could not begin synchronized character XP polling: {ex.Message}");
        }
    }

    internal static void NoteMonsterDeath(MonsterController monster, Damage damage)
    {
        if (!_worldReady || _localPlayer is null || monster is null)
            return;
        try
        {
            if (monster.DisableDropsAndExp)
                return;
        }
        catch
        {
            // Continue when a transitioning network object cannot expose this
            // optional server-authored hint. Attribution still fails closed.
        }
        if (!IsRelevantAttacker(damage.AttackerId, damage.IsSummon || damage.IsClone))
            return;

        string typeId = SafeMonsterType(monster);
        string name = MonsterNameOnly(SafeDisplayName(monster, typeId), typeId);
        if (string.IsNullOrWhiteSpace(typeId))
            typeId = name;
        if (string.IsNullOrWhiteSpace(name))
            name = "Unknown monster";

        double now = Time.realtimeSinceStartupAsDouble;
        PruneCorrelation(now);
        var source = new SourceCandidate(typeId, name, now);

        PendingGain pending = PendingGains
            .Where(gain => Math.Abs(now - gain.At) <= AttributionWindow)
            .OrderBy(gain => Math.Abs(now - gain.At))
            .FirstOrDefault();
        if (pending is not null)
        {
            PendingGains.Remove(pending);
            RemoveGain(RowsByName, UnattributedKey, pending.BaseXp, pending.JobXp);
            RemoveGain(RowsByType, UnattributedKey, pending.BaseXp, pending.JobXp);
            AddAttributedGain(source, pending.BaseXp, pending.JobXp, pending.At);
            source.Consumed = true;
            return;
        }

        RecentDeaths.Add(source);
    }

    private static float AttributionWindow => Mathf.Clamp(
        Plugin.Settings.AttributionWindow.Value, 0.25f, 5f);

    private static SourceCandidate FindBestDeath(double now) => RecentDeaths
        .Where(death => !death.Consumed && Math.Abs(now - death.At) <= AttributionWindow)
        .OrderBy(death => Math.Abs(now - death.At))
        .FirstOrDefault();

    private static void AddAttributedGain(SourceCandidate source, long baseXp, long jobXp, double at)
    {
        AddGain(RowsByName, source.Name, source.Name, baseXp, jobXp, 1, at);
        AddGain(RowsByType, source.TypeId, source.TypeId, baseXp, jobXp, 1, at);
        _attributedKills++;
        if (!_loggedFirstAttribution)
        {
            _loggedFirstAttribution = true;
            Plugin.Info($"First monster XP attribution matched to '{source.Name}' ({source.TypeId}).");
        }
    }

    private static void AddGain(Dictionary<string, SourceRow> rows, string key, string name,
        long baseXp, long jobXp, int kills, double at)
    {
        if (!rows.TryGetValue(key, out SourceRow row))
        {
            row = new SourceRow(key, name);
            rows.Add(key, row);
        }
        row.Name = name;
        row.BaseXp += baseXp;
        row.JobXp += jobXp;
        row.Kills += kills;
        row.LastAt = Math.Max(row.LastAt, at);
    }

    private static void RemoveGain(Dictionary<string, SourceRow> rows, string key,
        long baseXp, long jobXp)
    {
        if (!rows.TryGetValue(key, out SourceRow row))
            return;
        row.BaseXp = Math.Max(0L, row.BaseXp - baseXp);
        row.JobXp = Math.Max(0L, row.JobXp - jobXp);
        if (row.BaseXp == 0L && row.JobXp == 0L && row.Kills == 0)
            rows.Remove(key);
    }

    private static void PruneCorrelation(double now)
    {
        double cutoff = now - AttributionWindow;
        RecentDeaths.RemoveAll(death => death.Consumed || death.At < cutoff);
        PendingGains.RemoveAll(gain => gain.At < cutoff);
    }

    private static void CaptureBaseline()
    {
        try
        {
            CharacterData data = _localPlayer?.Save?.Data;
            if (data is null)
            {
                _hasBaseline = false;
                return;
            }
            _lastBaseExp = data.Exp;
            _lastJobExp = data.JobExp;
            _hasBaseline = true;
        }
        catch
        {
            _hasBaseline = false;
        }
    }

    private static void ClearSession(bool preserveRecentDeaths = false)
    {
        RowsByName.Clear();
        RowsByType.Clear();
        if (!preserveRecentDeaths)
            RecentDeaths.Clear();
        PendingGains.Clear();
        _totalBaseXp = 0L;
        _totalJobXp = 0L;
        _attributedKills = 0L;
        _startedAt = -1d;
        _lastGainAt = -1d;
        _loggedFirstGain = false;
        _loggedFirstAttribution = false;
        _loggedUnattributed = false;
        if (_text is not null)
            _text.text = BuildDisplayText(Time.realtimeSinceStartupAsDouble);
    }

    private static void EnsureUi()
    {
        if (!_worldReady || _canvasObject is not null)
            return;

        _canvasObject = new GameObject("NevarisXpMeter.Canvas");
        Object.DontDestroyOnLoad(_canvasObject);
        _canvas = _canvasObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 32000;
        CanvasScaler scaler = _canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        _canvasObject.AddComponent<GraphicRaycaster>();

        _panelObject = new GameObject("Panel");
        _panelObject.transform.SetParent(_canvasObject.transform, false);
        _panelRect = _panelObject.AddComponent<RectTransform>();
        _panelRect.anchorMin = new Vector2(0f, 1f);
        _panelRect.anchorMax = new Vector2(0f, 1f);
        _panelRect.pivot = new Vector2(0f, 1f);
        _panelRect.anchoredPosition = new Vector2(
            Plugin.Settings.PositionX.Value, Plugin.Settings.PositionY.Value);
        _panelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        _preferredUiScale = Mathf.Clamp(
            Plugin.Settings.UiScale.Value, MinimumUserScale, MaximumUserScale);
        _background = _panelObject.AddComponent<Image>();
        _background.color = new Color(0.035f, 0.045f, 0.06f, 0.94f);
        Outline outline = _panelObject.AddComponent<Outline>();
        outline.effectColor = new Color(0.68f, 0.54f, 0.28f, 0.7f);
        outline.effectDistance = new Vector2(1f, -1f);

        GameObject headerObject = new("DragHandle");
        headerObject.transform.SetParent(_panelObject.transform, false);
        _dragHandleRect = headerObject.AddComponent<RectTransform>();
        _dragHandleRect.anchorMin = new Vector2(0f, 1f);
        _dragHandleRect.anchorMax = new Vector2(1f, 1f);
        _dragHandleRect.pivot = new Vector2(0.5f, 1f);
        _dragHandleRect.offsetMin = new Vector2(0f, -37f);
        _dragHandleRect.offsetMax = Vector2.zero;
        Image headerBackground = headerObject.AddComponent<Image>();
        headerBackground.color = new Color(0.055f, 0.07f, 0.08f, 0.96f);

        GameObject accentObject = new("AccentRule");
        accentObject.transform.SetParent(_panelObject.transform, false);
        RectTransform accentRect = accentObject.AddComponent<RectTransform>();
        accentRect.anchorMin = new Vector2(0f, 1f);
        accentRect.anchorMax = new Vector2(1f, 1f);
        accentRect.pivot = new Vector2(0.5f, 1f);
        accentRect.offsetMin = new Vector2(0f, -39f);
        accentRect.offsetMax = new Vector2(0f, -37f);
        Image accent = accentObject.AddComponent<Image>();
        accent.color = new Color(0.73f, 0.58f, 0.3f, 0.9f);

        _titleText = CreateText("Title", headerObject.transform);
        RectTransform titleRect = _titleText.rectTransform;
        titleRect.anchorMin = Vector2.zero;
        titleRect.anchorMax = Vector2.one;
        titleRect.offsetMin = new Vector2(13f, 5f);
        titleRect.offsetMax = new Vector2(-58f, -4f);
        _titleText.fontSize = 14f;
        _titleText.fontStyle = FontStyles.Bold;
        _titleText.alignment = TextAlignmentOptions.MidlineLeft;
        _titleText.color = new Color(0.91f, 0.82f, 0.62f, 1f);
        _titleText.text = "XP REPORT";

        _dragHintText = CreateText("DragHint", headerObject.transform);
        RectTransform hintRect = _dragHintText.rectTransform;
        hintRect.anchorMin = Vector2.zero;
        hintRect.anchorMax = Vector2.one;
        hintRect.offsetMin = new Vector2(350f, 5f);
        hintRect.offsetMax = new Vector2(-13f, -4f);
        _dragHintText.fontSize = 9f;
        _dragHintText.alignment = TextAlignmentOptions.MidlineRight;
        _dragHintText.color = new Color(0.63f, 0.68f, 0.69f, 1f);
        _dragHintText.text = "DRAG";

        GameObject textObject = new("Text");
        textObject.transform.SetParent(_panelObject.transform, false);
        RectTransform textRect = textObject.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(13f, 10f);
        textRect.offsetMax = new Vector2(-13f, -49f);
        _text = textObject.AddComponent<TextMeshProUGUI>();
        _text.fontSize = 11.5f;
        _text.alignment = TextAlignmentOptions.TopLeft;
        _text.color = new Color(0.88f, 0.91f, 0.91f, 1f);
        _text.richText = true;
        _text.textWrappingMode = TextWrappingModes.NoWrap;
        _text.overflowMode = TextOverflowModes.Overflow;
        _text.raycastTarget = false;
        _text.lineSpacing = 2.25f;
        _text.text = BuildDisplayText(Time.realtimeSinceStartupAsDouble);

        GameObject resizeObject = new("ResizeHandle");
        resizeObject.transform.SetParent(_panelObject.transform, false);
        _resizeHandleRect = resizeObject.AddComponent<RectTransform>();
        _resizeHandleRect.anchorMin = new Vector2(1f, 0f);
        _resizeHandleRect.anchorMax = new Vector2(1f, 0f);
        _resizeHandleRect.pivot = new Vector2(1f, 0f);
        _resizeHandleRect.anchoredPosition = new Vector2(-3f, 3f);
        _resizeHandleRect.sizeDelta = new Vector2(28f, 28f);
        Image resizeHitArea = resizeObject.AddComponent<Image>();
        resizeHitArea.color = new Color(0.73f, 0.58f, 0.3f, 0.04f);
        CreateResizeGripLine(resizeObject.transform, 12f, new Vector2(-7f, 7f));
        CreateResizeGripLine(resizeObject.transform, 8f, new Vector2(-5f, 5f));
        CreateResizeGripLine(resizeObject.transform, 4f, new Vector2(-3f, 3f));

        _gameStyleApplied = TryApplyGameStyle();
        ApplyScaleForScreen();
        ClampPanelToScreen();
        _panelObject.SetActive(_visible && !IsMenuOpen());
    }

    private static string BuildDisplayText(double now)
    {
        bool showBase = Plugin.Settings.ShowBaseXp.Value;
        bool showJob = Plugin.Settings.ShowJobXp.Value;
        if (!showBase && !showJob)
        {
            return "<color=#E8D19B><b>NO XP COLUMNS ENABLED</b></color>\n"
                + "<size=10><color=#9AA8AA>Enable base XP or job XP in MCM.</color></size>";
        }

        if (_totalBaseXp == 0L && _totalJobXp == 0L)
        {
            return "<color=#E8D19B><b>WAITING FOR XP</b></color>\n"
                + "<size=10><color=#9AA8AA>Base and job awards will be grouped by monster here.</color></size>";
        }

        double elapsed = GetRateSeconds(now);
        var summary = new List<string>();
        if (showBase)
            summary.Add($"<size=10><color=#9AA8AA>BASE</color></size>  <size=18><b>{Format(_totalBaseXp)}</b></size>");
        if (showJob)
            summary.Add($"<size=10><color=#9AA8AA>JOB</color></size>  <size=18><b>{Format(_totalJobXp)}</b></size>");
        string elapsedText = FormatDuration(Math.Max(0d, now - _startedAt));
        if (Plugin.Settings.ShowKills.Value)
            elapsedText += $"  {_attributedKills:N0} kills";
        string firstLine = string.Join("   ", summary)
            + $"   <size=10><color=#9AA8AA>{elapsedText}</color></size>";

        var lines = new List<string> { firstLine };
        if (Plugin.Settings.ShowRates.Value)
        {
            var rates = new List<string>();
            if (showBase)
                rates.Add($"BASE/H  <b>{Format(RatePerHour(_totalBaseXp, elapsed))}</b>");
            if (showJob)
                rates.Add($"JOB/H  <b>{Format(RatePerHour(_totalJobXp, elapsed))}</b>");
            lines.Add($"<size=10><color=#9AA8AA>{string.Join("     ", rates)}</color></size>");
        }

        lines.Add(string.Empty);
        ColumnLayout columns = BuildColumns(showBase, showJob);
        lines.Add(columns.Header);

        IEnumerable<SourceRow> sourceRows = string.Equals(
            Plugin.Settings.GroupBy.Value, MeterSettings.GroupMonsterType, StringComparison.Ordinal)
            ? RowsByType.Values
            : RowsByName.Values;
        if (!Plugin.Settings.ShowUnattributed.Value)
            sourceRows = sourceRows.Where(row => row.Key != UnattributedKey);

        int rank = 1;
        int maxRows = Math.Clamp(Plugin.Settings.MaxRows.Value, 1, 20);
        foreach (SourceRow row in SortRows(sourceRows).Take(maxRows))
        {
            string displayName = row.Key == UnattributedKey ? UnattributedLabel : row.Name;
            string line = $"<color=#D6B979>{rank,2}</color>  {FitSourceName(displayName)}";
            if (columns.KillsX > 0)
                line += $"<pos={columns.KillsX}>{row.Kills}";
            if (columns.BaseX > 0)
                line += $"<pos={columns.BaseX}><b>{Format(row.BaseXp)}</b>";
            if (columns.JobX > 0)
                line += $"<pos={columns.JobX}><b>{Format(row.JobXp)}</b>";
            if (columns.ShareX > 0)
            {
                long numerator = DisplayMetric(row, showBase, showJob);
                long denominator = DisplayTotal(showBase, showJob);
                double share = denominator > 0L ? (double)numerator / denominator : 0d;
                line += $"<pos={columns.ShareX}>{share:P0}";
            }
            lines.Add(line);
            rank++;
        }

        if (rank == 1)
            lines.Add("<size=10><color=#9AA8AA>No attributed rows match the current display filters.</color></size>");
        return string.Join("\n", lines);
    }

    private static ColumnLayout BuildColumns(bool showBase, bool showJob)
    {
        int x = 177;
        var header = new System.Text.StringBuilder(
            "<size=9><color=#829092>    SOURCE");
        var layout = new ColumnLayout();
        if (Plugin.Settings.ShowKills.Value)
        {
            layout.KillsX = x;
            header.Append($"<pos={x}>KILLS");
            x += 50;
        }
        if (showBase)
        {
            layout.BaseX = x;
            header.Append($"<pos={x}>BASE XP");
            x += 70;
        }
        if (showJob)
        {
            layout.JobX = x;
            header.Append($"<pos={x}>JOB XP");
            x += 68;
        }
        if (Plugin.Settings.ShowPercentages.Value && x <= 375)
        {
            layout.ShareX = x;
            header.Append($"<pos={x}>SHARE");
        }
        header.Append("</color></size>");
        layout.Header = header.ToString();
        return layout;
    }

    private static IOrderedEnumerable<SourceRow> SortRows(IEnumerable<SourceRow> rows)
    {
        string sort = Plugin.Settings.SortBy.Value;
        if (sort == MeterSettings.SortBaseXp)
            return rows.OrderByDescending(row => row.BaseXp).ThenBy(row => row.Name);
        if (sort == MeterSettings.SortJobXp)
            return rows.OrderByDescending(row => row.JobXp).ThenBy(row => row.Name);
        if (sort == MeterSettings.SortKills)
            return rows.OrderByDescending(row => row.Kills).ThenByDescending(row => row.BaseXp + row.JobXp);
        if (sort == MeterSettings.SortRecent)
            return rows.OrderByDescending(row => row.LastAt).ThenBy(row => row.Name);
        if (sort == MeterSettings.SortName)
            return rows.OrderBy(row => row.Name).ThenByDescending(row => row.BaseXp + row.JobXp);
        return rows.OrderByDescending(row => row.BaseXp + row.JobXp).ThenBy(row => row.Name);
    }

    private static long DisplayMetric(SourceRow row, bool showBase, bool showJob)
    {
        if (showBase && showJob) return row.BaseXp + row.JobXp;
        return showBase ? row.BaseXp : row.JobXp;
    }

    private static long DisplayTotal(bool showBase, bool showJob)
    {
        if (showBase && showJob) return _totalBaseXp + _totalJobXp;
        return showBase ? _totalBaseXp : _totalJobXp;
    }

    private static double GetRateSeconds(double now)
    {
        if (_startedAt < 0d)
            return 0d;
        bool active = string.Equals(
            Plugin.Settings.RateClock.Value, MeterSettings.RateActive, StringComparison.Ordinal);
        double end = active && _lastGainAt >= 0d ? _lastGainAt : now;
        return Math.Max(1d, end - _startedAt);
    }

    private static long RatePerHour(long value, double seconds)
    {
        if (value <= 0L || seconds <= 0d)
            return 0L;
        double rate = value * 3600d / seconds;
        return rate >= long.MaxValue ? long.MaxValue : (long)Math.Round(rate);
    }

    private static string UnattributedLabel
    {
        get
        {
            string label = Plugin.Settings.UnattributedLabel.Value;
            return string.IsNullOrWhiteSpace(label) ? "Other" : label.Trim();
        }
    }

    private static string FitSourceName(string value)
    {
        string name = MonsterNameOnly(value, "Unknown");
        name = name.Trim();
        if (SourceNameFits(name))
            return EscapeMarkup(name);

        int[] textElements = StringInfo.ParseCombiningCharacters(name);
        for (int count = textElements.Length - 1; count > 0; count--)
        {
            string candidate = name[..textElements[count]].TrimEnd() + "...";
            if (SourceNameFits(candidate))
                return EscapeMarkup(candidate);
        }

        return "...";
    }

    private static string MonsterNameOnly(string value, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        int suffixStart = name.Length;
        int newline = name.IndexOfAny(new[] { '\r', '\n' });
        int richText = name.IndexOf('<');
        int escapedRichText = name.IndexOf("&lt;", StringComparison.OrdinalIgnoreCase);
        if (newline >= 0)
            suffixStart = Math.Min(suffixStart, newline);
        if (richText >= 0)
            suffixStart = Math.Min(suffixStart, richText);
        if (escapedRichText >= 0)
            suffixStart = Math.Min(suffixStart, escapedRichText);
        if (suffixStart < name.Length)
            name = name[..suffixStart].TrimEnd();
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static bool SourceNameFits(string value)
    {
        if (_text is null)
            return value.Length <= 15;
        try
        {
            // TMP measures the active game font, so wide glyphs and localized
            // names stop at the same visual boundary as narrow Latin names.
            return _text.GetPreferredValues(EscapeMarkup(value)).x <= SourceNameMaxWidth;
        }
        catch
        {
            return value.Length <= 15;
        }
    }

    private static string EscapeMarkup(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private static string Format(long value)
    {
        if (!Plugin.Settings.AbbreviateNumbers.Value)
            return value.ToString("N0");
        if (value >= 1_000_000_000) return $"{value / 1_000_000_000d:0.##}b";
        if (value >= 1_000_000) return $"{value / 1_000_000d:0.##}m";
        if (value >= 1_000) return $"{value / 1_000d:0.##}k";
        return value.ToString();
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60d) return $"{seconds:0}s";
        if (seconds < 3600d) return $"{Math.Floor(seconds / 60d):0}m {seconds % 60d:00}s";
        return $"{Math.Floor(seconds / 3600d):0}h {Math.Floor(seconds % 3600d / 60d):00}m";
    }

    private static TextMeshProUGUI CreateText(string name, Transform parent)
    {
        GameObject obj = new(name);
        obj.transform.SetParent(parent, false);
        TextMeshProUGUI text = obj.AddComponent<TextMeshProUGUI>();
        text.raycastTarget = false;
        text.richText = true;
        return text;
    }

    private static void CreateResizeGripLine(Transform parent, float width, Vector2 position)
    {
        GameObject lineObject = new($"GripLine{width:0}");
        lineObject.transform.SetParent(parent, false);
        RectTransform lineRect = lineObject.AddComponent<RectTransform>();
        lineRect.anchorMin = new Vector2(1f, 0f);
        lineRect.anchorMax = new Vector2(1f, 0f);
        lineRect.pivot = new Vector2(1f, 0.5f);
        lineRect.anchoredPosition = position;
        lineRect.sizeDelta = new Vector2(width, 1.5f);
        lineRect.localEulerAngles = new Vector3(0f, 0f, -45f);
        Image line = lineObject.AddComponent<Image>();
        line.color = new Color(0.82f, 0.69f, 0.42f, 0.9f);
        line.raycastTarget = false;
    }

    private static bool TryApplyGameStyle()
    {
        if (_background is null || _text is null)
            return false;
        try
        {
            UIGame game = App.UI?.Game;
            UIChat chat = game?.Chat ?? UIChat.Instance;
            if (game is null || chat is null)
                return false;

            Sprite panelSprite = chat.BackgroundSprite;
            if (panelSprite is not null)
            {
                _background.sprite = panelSprite;
                _background.type = Image.Type.Sliced;
                float ppu = chat.BackgroundPpu;
                if (ppu > 0f)
                    _background.pixelsPerUnitMultiplier = ppu;
            }

            Color panelColor = chat.BackgroundColor;
            panelColor.a = Mathf.Max(panelColor.a, 0.92f);
            _background.color = panelColor;

            TMP_Text source = chat.gameObject.GetComponentInChildren<TMP_Text>(true)
                ?? game.gameObject.GetComponentInChildren<TMP_Text>(true);
            if (source is not null && source.font is not null)
            {
                _titleText.font = source.font;
                _dragHintText.font = source.font;
                _text.font = source.font;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMenuOpen()
    {
        try
        {
            UIManager ui = App.UI;
            UIGame game = ui?.Game;
            if (ui is null || game is null)
                return true;
            if (!ui.IsInGame || !ui.IsShowingGameScreen || ui.IsLoading)
                return true;
            if (!game.gameObject.activeInHierarchy || UIPopup.HasPopup)
                return true;
            return game.IsFocused;
        }
        catch
        {
            return true;
        }
    }

    private static void HandlePointerInteractions()
    {
        if (_panelRect is null || _dragHandleRect is null
            || _resizeHandleRect is null || _canvas is null)
            return;

        Vector2 mouse = Input.mousePosition;
        if (Input.GetMouseButtonDown(0))
        {
            if (RectTransformUtility.RectangleContainsScreenPoint(_resizeHandleRect, mouse, null))
            {
                _resizing = true;
                _dragging = false;
                _resizeStartMouse = mouse;
                _resizeStartScale = _preferredUiScale;
            }
            else if (RectTransformUtility.RectangleContainsScreenPoint(_dragHandleRect, mouse, null))
            {
                _dragging = true;
                _resizing = false;
                _dragStartMouse = mouse;
                _dragStartPosition = _panelRect.anchoredPosition;
            }
        }

        if (_dragging && Input.GetMouseButton(0))
        {
            float scale = Mathf.Max(0.01f, _canvas.scaleFactor);
            _panelRect.anchoredPosition = _dragStartPosition + (mouse - _dragStartMouse) / scale;
            ClampPanelToScreen();
        }

        if (_resizing && Input.GetMouseButton(0))
        {
            Vector2 delta = mouse - _resizeStartMouse;
            float canvasScale = Mathf.Max(0.01f, _canvas.scaleFactor);
            float scaleDelta = (delta.x - delta.y) / (300f * canvasScale);
            _preferredUiScale = Mathf.Clamp(
                _resizeStartScale + scaleDelta, MinimumUserScale, MaximumUserScale);
            ApplyScaleForScreen();
            ClampPanelToScreen();
        }

        if (!Input.GetMouseButtonUp(0))
            return;
        if (_dragging)
        {
            _dragging = false;
            Plugin.Settings.PositionX.Value = _panelRect.anchoredPosition.x;
            Plugin.Settings.PositionY.Value = _panelRect.anchoredPosition.y;
        }
        if (_resizing)
        {
            _resizing = false;
            Plugin.Settings.UiScale.Value = _preferredUiScale;
        }
    }

    private static void ApplyScaleForScreen()
    {
        if (_panelRect is null || _canvas is null)
            return;
        float canvasScale = Mathf.Max(0.01f, _canvas.scaleFactor);
        float availableWidth = Mathf.Max(1f, Screen.width / canvasScale - 16f);
        float availableHeight = Mathf.Max(1f, Screen.height / canvasScale - 16f);
        float fitScale = Mathf.Min(availableWidth / PanelWidth, availableHeight / PanelHeight);
        float effectiveScale = Mathf.Min(_preferredUiScale, fitScale);
        effectiveScale = Mathf.Clamp(effectiveScale, MinimumAutoFitScale, MaximumUserScale);
        _panelRect.localScale = new Vector3(effectiveScale, effectiveScale, 1f);
        _lastScreenWidth = Screen.width;
        _lastScreenHeight = Screen.height;
    }

    private static void ClampPanelToScreen()
    {
        if (_panelRect is null || _canvas is null)
            return;
        float scale = Mathf.Max(0.01f, _canvas.scaleFactor);
        float width = Screen.width / scale;
        float height = Screen.height / scale;
        Vector2 size = _panelRect.sizeDelta * _panelRect.localScale.x;
        Vector2 position = _panelRect.anchoredPosition;
        position.x = Mathf.Clamp(position.x, 8f, Mathf.Max(8f, width - size.x - 8f));
        position.y = Mathf.Clamp(position.y, -Mathf.Max(8f, height - size.y - 8f), -8f);
        _panelRect.anchoredPosition = position;
    }

    private static void DestroyUi()
    {
        if (_canvasObject is not null)
            Object.Destroy(_canvasObject);
        _canvasObject = null;
        _panelObject = null;
        _canvas = null;
        _panelRect = null;
        _dragHandleRect = null;
        _resizeHandleRect = null;
        _background = null;
        _titleText = null;
        _dragHintText = null;
        _text = null;
        _dragging = false;
        _resizing = false;
        _gameStyleApplied = false;
    }

    private static bool IsRelevantAttacker(int attackerId, bool ownedEntity)
    {
        BaseUnitController attacker = attackerId == _localPlayer.ObjectId
            ? _localPlayer
            : ResolveUnit(attackerId);
        if (attacker is null)
            return false;
        if (ownedEntity)
            attacker = ResolveSummonOwner(attacker) ?? attacker;
        PlayerController player = GetPlayerController(attacker);
        return player is not null && IsLocalOrPartyMember(player);
    }

    private static BaseUnitController ResolveUnit(int objectId)
    {
        if (objectId < 0 || !InstanceFinder.IsClientStarted)
            return null;
        var objects = InstanceFinder.ClientManager?.Objects?.Spawned;
        if (objects is null || !objects.TryGetValue(objectId, out NetworkObject networkObject)
            || networkObject is null)
            return null;
        return networkObject.GetComponent<BaseUnitController>();
    }

    private static BaseUnitController ResolveSummonOwner(BaseUnitController unit)
    {
        BaseUnitController current = unit;
        for (int i = 0; i < 3; i++)
        {
            BaseUnitController owner = current.Summoning?.Summoner;
            if (owner is null || owner == current)
                break;
            current = owner;
        }
        return current;
    }

    private static PlayerController GetPlayerController(BaseUnitController unit)
    {
        try { return unit.gameObject.GetComponent<PlayerController>(); }
        catch { return null; }
    }

    private static bool IsLocalOrPartyMember(PlayerController player)
    {
        try
        {
            if (_localPlayer.ObjectId == player.ObjectId)
                return true;
            if (!_localPlayer.HasParty || _localPlayer.CurrentParty is null)
                return false;
            string playerId = player.PlayerId ?? string.Empty;
            return !string.IsNullOrWhiteSpace(playerId)
                && _localPlayer.CurrentParty.HasMember(playerId)
                && _localPlayer.IsInMyParty(player);
        }
        catch
        {
            return false;
        }
    }

    private static string SafeMonsterType(MonsterController monster)
    {
        try { return monster.ConfigId ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeDisplayName(BaseUnitController unit, string fallback)
    {
        try
        {
            string value = unit.DisplayName;
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch { return fallback; }
    }

    private static string SafeCharacterId(PlayerController player)
    {
        try { return player.Save?.Data?.UID ?? player.PlayerId ?? string.Empty; }
        catch { return string.Empty; }
    }

    private sealed class SourceRow
    {
        internal SourceRow(string key, string name)
        {
            Key = key;
            Name = name;
        }

        internal readonly string Key;
        internal string Name;
        internal long BaseXp;
        internal long JobXp;
        internal int Kills;
        internal double LastAt;
    }

    private sealed class SourceCandidate
    {
        internal SourceCandidate(string typeId, string name, double at)
        {
            TypeId = typeId;
            Name = name;
            At = at;
        }

        internal readonly string TypeId;
        internal readonly string Name;
        internal readonly double At;
        internal bool Consumed;
    }

    private sealed class PendingGain
    {
        internal PendingGain(long baseXp, long jobXp, double at)
        {
            BaseXp = baseXp;
            JobXp = jobXp;
            At = at;
        }

        internal readonly long BaseXp;
        internal readonly long JobXp;
        internal readonly double At;
    }

    private sealed class ColumnLayout
    {
        internal string Header;
        internal int KillsX;
        internal int BaseX;
        internal int JobX;
        internal int ShareX;
    }
}
