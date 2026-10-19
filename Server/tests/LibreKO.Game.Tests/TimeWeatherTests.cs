using FluentAssertions;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Configuration;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class TimeWeatherTests
{
    [Fact]
    public void BuildCurrentTimePacket_ReportsCurrentGameTime()
    {
        var service = Create();

        var time = service.BuildCurrentTimePacket();
        var packetDate = new DateTime(time.ReadShort(), time.ReadShort(), time.ReadShort());

        packetDate.Should().BeOneOf(DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(-1));
        time.ReadShort().Should().BeInRange((short)0, (short)23);
        time.ReadShort().Should().BeInRange((short)0, (short)59);
    }

    [Theory]
    [InlineData(18, 0)]
    [InlineData(0, 0)]
    [InlineData(23, 59)]
    public void SetTimeOfDay_MovesTheBroadcastClockToThatTime(int hour, int minute)
    {
        var service = Create();

        service.SetTimeOfDay(hour, minute);

        service.MinuteOfDay.Should().Be(hour * 60 + minute);
        var time = service.BuildCurrentTimePacket();
        time.ReadShort();
        time.ReadShort();
        time.ReadShort();
        time.ReadShort().Should().Be((short)hour);
        time.ReadShort().Should().Be((short)minute);
    }

    [Theory]
    [InlineData("18", 18, 0)]
    [InlineData("0", 0, 0)]
    [InlineData("18:20", 18, 20)]
    [InlineData(" 6 : 05 ", 6, 5)]
    public void TryParseTimeOfDay_AcceptsHourAndHourMinute(string text, int hour, int minute)
    {
        TimeWeatherBroadcastService.TryParseTimeOfDay(text, out var h, out var m).Should().BeTrue();
        h.Should().Be(hour);
        m.Should().Be(minute);
    }

    [Theory]
    [InlineData("")]
    [InlineData("24")]
    [InlineData("-1")]
    [InlineData("18:60")]
    [InlineData("18:20:30")]
    [InlineData("dusk")]
    public void TryParseTimeOfDay_RejectsAnythingElse(string text)
    {
        TimeWeatherBroadcastService.TryParseTimeOfDay(text, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TrySetWeather_RoundTripsThroughTheWeatherPacket()
    {
        var service = Create();

        service.TrySetWeather(TimeWeatherBroadcastService.WeatherSnow, 80).Should().BeTrue();

        var weather = service.BuildCurrentWeatherPacket();
        weather.ReadByte().Should().Be(TimeWeatherBroadcastService.WeatherSnow);
        weather.ReadUShort().Should().Be(80);
    }

    [Fact]
    public void DefaultWeather_IsClear()
    {
        var weather = Create().BuildCurrentWeatherPacket();

        weather.ReadByte().Should().Be(TimeWeatherBroadcastService.WeatherFine);
        weather.ReadUShort().Should().Be(0);
    }

    [Fact]
    public void FixedTimeOfDay_HoldsTheBroadcastClock()
    {
        var service = Create(new TimeWeatherSettings { FixedTimeOfDay = "12:00" });

        service.MinuteOfDay.Should().Be(12 * 60);
        var time = service.BuildCurrentTimePacket();
        time.ReadShort();
        time.ReadShort();
        time.ReadShort();
        time.ReadShort().Should().Be(12);
        time.ReadShort().Should().Be(0);
    }

    [Fact]
    public void SetTimeOfDay_UnderAFixedTime_HoldsTheNewTimeUntilReset()
    {
        var service = Create(new TimeWeatherSettings { FixedTimeOfDay = "12:00" });

        service.SetTimeOfDay(18, 30);
        service.MinuteOfDay.Should().Be(18 * 60 + 30);

        service.ResetTimeOfDay();
        service.MinuteOfDay.Should().Be(12 * 60);
    }

    [Fact]
    public void FixedWeather_StartsThereAndTheHourlyRollKeepsIt()
    {
        var service = Create(new TimeWeatherSettings { FixedWeather = "Rain" });
        service.WeatherType.Should().Be(TimeWeatherBroadcastService.WeatherRain);

        for (var roll = 0; roll < WeatherRolls; roll++)
        {
            service.RefreshWeather();
            var weather = service.BuildCurrentWeatherPacket();
            weather.ReadByte().Should().Be(TimeWeatherBroadcastService.WeatherRain);
            weather.ReadUShort().Should().Be(0);
        }
    }

    [Fact]
    public void FixedWeather_ComesBackAtTheNextHourlyTickAfterAnOverride()
    {
        var service = Create(new TimeWeatherSettings { FixedWeather = "Fine" });

        service.TrySetWeather(TimeWeatherBroadcastService.WeatherSnow, OverrideAmount);
        service.WeatherType.Should().Be(TimeWeatherBroadcastService.WeatherSnow);

        service.RefreshWeather();
        var weather = service.BuildCurrentWeatherPacket();
        weather.ReadByte().Should().Be(TimeWeatherBroadcastService.WeatherFine);
        weather.ReadUShort().Should().Be(0);
    }

    [Fact]
    public void FixedWeather_CanBeOtherThanClear()
    {
        var service = Create(new TimeWeatherSettings { FixedWeather = "Snow" });

        service.RefreshWeather();

        service.BuildCurrentWeatherPacket().ReadByte().Should().Be(TimeWeatherBroadcastService.WeatherSnow);
    }

    [Fact]
    public void WithoutFixedWeather_TheHourlyRollStillChangesTheWeather()
    {
        var service = Create();
        var seen = new HashSet<byte>();

        for (var roll = 0; roll < WeatherRolls; roll++)
        {
            service.RefreshWeather();
            seen.Add(service.WeatherType);
        }

        seen.Should().Contain(TimeWeatherBroadcastService.WeatherFine).And.HaveCountGreaterThan(1);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("12:60")]
    [InlineData("noon")]
    public void InvalidFixedTimeOfDay_StopsTheServerFromStarting(string text)
    {
        var create = () => Create(new TimeWeatherSettings { FixedTimeOfDay = text });

        create.Should().Throw<InvalidOperationException>().WithMessage("*GameServer:TimeWeather:FixedTimeOfDay*");
    }

    [Theory]
    [InlineData("Sunny")]
    [InlineData("fine")]
    [InlineData("1")]
    public void InvalidFixedWeather_StopsTheServerFromStarting(string text)
    {
        var create = () => Create(new TimeWeatherSettings { FixedWeather = text });

        create.Should().Throw<InvalidOperationException>().WithMessage("*GameServer:TimeWeather:FixedWeather*Fine*");
    }

    private const int WeatherRolls = 1000;
    private const ushort OverrideAmount = 80;

    private static TimeWeatherBroadcastService Create(TimeWeatherSettings? timeWeather = null) =>
        new(
            new SessionManager(),
            Options.Create(new GameServerSettings { TimeWeather = timeWeather ?? new TimeWeatherSettings() }),
            Substitute.For<ILogger<TimeWeatherBroadcastService>>());
}
