using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Gameplay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Configuration;
using LibreKO.Game.Protocol.Writers;
using Microsoft.Extensions.Options;

namespace LibreKO.Game.World;

public class TimeWeatherBroadcastService(
    SessionManager sessionManager,
    IOptions<GameServerSettings> settings,
    ILogger<TimeWeatherBroadcastService> logger) : BackgroundService
{
    private const int MinutesPerHour = 60;

    public const byte WeatherFine = (byte)Common.Enums.WeatherType.Fine;
    public const byte WeatherRain = (byte)Common.Enums.WeatherType.Rain;
    public const byte WeatherSnow = (byte)Common.Enums.WeatherType.Snow;
    public const byte WeatherLeaves = (byte)Common.Enums.WeatherType.Leaves;

    private const int SnowChance = 2;
    private const int RainChance = 7;
    private const int LeafChance = 12;
    private const int FineAmountFloor = 70;
    private const ushort FixedWeatherAmount = 0;

    // A full in-game day/night cycle takes this many REAL seconds (4 hours), so game time runs 6x.
    public const int DayCycleSeconds = 4 * 3600;
    private const int MinutesPerDay = 24 * 60;

    private const int TimeIntervalSeconds = 10;     // frequent enough that clients sync fast + interpolate
    private const int WeatherIntervalSeconds = 3600;

    private readonly int? fixedMinuteOfDay = ParseFixedTimeOfDay(settings.Value.TimeWeather.FixedTimeOfDay);
    private readonly Common.Enums.WeatherType? fixedWeather = ParseFixedWeather(settings.Value.TimeWeather.FixedWeather);

    private int minuteOffset;

    public int MinuteOfDay => Wrap(BaseMinuteOfDay + minuteOffset);

    public byte WeatherType { get; set; } =
        (byte)(ParseFixedWeather(settings.Value.TimeWeather.FixedWeather) ?? Common.Enums.WeatherType.Fine);
    public ushort WeatherAmount { get; set; }
    // GM event-rate bonuses (server-wide, percent). Applied multiplicatively on top of base.
    public byte ExpEventAmount { get; set; }
    public byte CoinEventAmount { get; set; }
    public byte NpEventAmount { get; set; }
    public byte DropEventAmount { get; set; }
    public byte SantaOrAngel { get; set; }

    public long ApplyExpBonus(long baseExp) =>
        ExpEventAmount == 0 ? baseExp : baseExp * (100 + ExpEventAmount) / 100;

    public int ApplyCoinBonus(int baseAmount) =>
        CoinEventAmount == 0 ? baseAmount : (int)((long)baseAmount * (100 + CoinEventAmount) / 100);

    public int ApplyNpBonus(int baseAmount) =>
        NpEventAmount == 0 || baseAmount <= 0
            ? baseAmount
            : (int)((long)baseAmount * (100 + NpEventAmount) / 100);

    public int ApplyDropBonus(int baseRate) =>
        DropEventAmount == 0 ? baseRate : (int)((long)baseRate * (100 + DropEventAmount) / 100);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Time/Weather broadcast service started (time {TimeSec}s, weather {WeatherSec}s, fixed time {FixedTime}, fixed weather {FixedWeather})",
            TimeIntervalSeconds, WeatherIntervalSeconds,
            fixedMinuteOfDay is { } fixedMinute ? $"{fixedMinute / MinutesPerHour:D2}:{fixedMinute % MinutesPerHour:D2}" : "off",
            fixedWeather?.ToString() ?? "off");

        var timeTask = RunTimeLoop(stoppingToken);
        var weatherTask = RunWeatherLoop(stoppingToken);
        await Task.WhenAll(timeTask, weatherTask);
    }

    private async Task RunTimeLoop(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TimeIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (sessionManager.OnlineCount == 0) continue;
                await sessionManager.BroadcastToAll(BuildCurrentTimePacket());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Time broadcast tick failed");
            }
        }
    }

    private async Task RunWeatherLoop(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(WeatherIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                RefreshWeather();
                if (sessionManager.OnlineCount == 0) continue;
                await BroadcastWeatherAsync();
                if (SantaOrAngel != 0)
                    await sessionManager.BroadcastToAll(MiscPacketWriter.SantaState(SantaOrAngel));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Weather broadcast tick failed");
            }
        }
    }

    public Packet BuildCurrentTimePacket() => BuildTimePacket(MinuteOfDay);

    public void SetTimeOfDay(int hour, int minute) =>
        minuteOffset = Wrap(hour * MinutesPerHour + minute - BaseMinuteOfDay);

    public void ResetTimeOfDay() => minuteOffset = 0;

    public static bool TryParseTimeOfDay(string text, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Trim().Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length > 2)
            return false;
        if (!int.TryParse(parts[0], out hour) || hour < 0 || hour > 23)
            return false;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out minute) || minute < 0 || minute > 59))
            return false;
        return true;
    }

    public Packet BuildCurrentWeatherPacket() => BuildWeatherPacket(WeatherType, WeatherAmount);

    public bool TrySetWeather(byte type, ushort amount)
    {
        WeatherType = type;
        WeatherAmount = amount;
        return true;
    }

    // Game time runs on a DayCycleSeconds-long loop (4 real hours = one full day/night), derived
    // from the UTC epoch so every client/restart agrees without persisting anything. `+time` shifts it
    // by a live offset, which therefore resets on restart.
    private static int EpochMinuteOfDay =>
        (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % DayCycleSeconds * MinutesPerDay / DayCycleSeconds);

    private int BaseMinuteOfDay => fixedMinuteOfDay ?? EpochMinuteOfDay;

    private static int Wrap(int minutes) => (minutes % MinutesPerDay + MinutesPerDay) % MinutesPerDay;

    private static int? ParseFixedTimeOfDay(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        if (!TryParseTimeOfDay(text, out var hour, out var minute))
            throw new InvalidOperationException(
                $"{SettingName(nameof(TimeWeatherSettings.FixedTimeOfDay))} must be a time of day such as 12:00, not '{text}'");
        return hour * MinutesPerHour + minute;
    }

    private static Common.Enums.WeatherType? ParseFixedWeather(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var names = Enum.GetNames<Common.Enums.WeatherType>();
        if (!names.Contains(text, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"{SettingName(nameof(TimeWeatherSettings.FixedWeather))} must be one of {string.Join(", ", names)}, not '{text}'");
        return Enum.Parse<Common.Enums.WeatherType>(text);
    }

    private static string SettingName(string key) =>
        $"{GameServerSettings.SectionName}:{nameof(GameServerSettings.TimeWeather)}:{key}";

    private static Packet BuildTimePacket(int minuteOfDay)
    {
        var now = DateTime.UtcNow;
        var pkt = TimeWeatherPacketWriter.GameTime(
            (short)now.Year, (short)now.Month, (short)now.Day,
            (short)(minuteOfDay / MinutesPerHour), (short)(minuteOfDay % MinutesPerHour));
        return pkt;
    }

    public static Packet BuildWeatherPacket(byte type, ushort amount)
    {
        var pkt = TimeWeatherPacketWriter.Weather(type, amount);
        return pkt;
    }

    internal void RefreshWeather()
    {
        if (fixedWeather is { } held)
            TrySetWeather((byte)held, FixedWeatherAmount);
        else
            RollWeather();
    }

    private void RollWeather()
    {
        var roll = Random.Shared.Next(0, 101);
        WeatherType = roll switch
        {
            < SnowChance => WeatherSnow,
            < RainChance => WeatherRain,
            < LeafChance => WeatherLeaves,
            _ => WeatherFine,
        };

        var amount = (ushort)Random.Shared.Next(0, 101);
        WeatherAmount = WeatherType == WeatherFine
            ? (ushort)(amount > FineAmountFloor ? amount / 2 : 0)
            : amount;
    }

    public Packet BuildWeatherPacketFor(byte zoneId)
        => IsAlwaysClear(zoneId)
            ? BuildWeatherPacket(WeatherFine, WeatherAmount)
            : BuildCurrentWeatherPacket();

    public async Task BroadcastWeatherAsync()
    {
        var real = BuildCurrentWeatherPacket();
        var clear = BuildWeatherPacket(WeatherFine, WeatherAmount);

        foreach (var session in sessionManager.GetAll())
        {
            var packet = IsAlwaysClear(session.ZoneId) ? clear : real;
            try { await session.Client.SendPacket(packet); }
            catch (Exception ex) { logger.LogDebug(ex, "Weather send failed for {Id}", session.CharacterId); }
        }
    }

    private static bool IsAlwaysClear(byte zoneId)
        => zoneId is (byte)ZoneId.DesperationAbyss or (byte)ZoneId.HellAbyss or (byte)ZoneId.Arena;
}
