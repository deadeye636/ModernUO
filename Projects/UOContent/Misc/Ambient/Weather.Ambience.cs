namespace Server.Misc;

// Reads the private cycle state of the regular weather for the ambience zones; a rename of these fields upstream
// breaks the build here on purpose.
public partial class Weather
{
    /// <summary>
    /// Whether the last tick sent rain or snow. <see cref="OnTick"/> advances the stage after sending, and stage 0
    /// sends density 0, so a cycle that just started (stage 1 now) is not precipitating yet.
    /// </summary>
    public bool IsPrecipitating => m_Active && m_Stage != 1;

    /// <summary>Snow rather than rain, by the same temperature rule <see cref="OnTick"/> uses.</summary>
    public bool IsSnowing => (m_ExtremeTemperature ? -Temperature : Temperature) <= 0;

    public bool CoversLocation(Point3D p)
    {
        if (Area.Length == 0)
        {
            return true;
        }

        for (var i = 0; i < Area.Length; i++)
        {
            if (Area[i].Contains(p))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first regular weather of <paramref name="map"/> that covers <paramref name="p"/> and is precipitating.</summary>
    public static bool TryGetPrecipitation(Map map, Point3D p, out bool snow)
    {
        if (map != null && m_WeatherByFacet.TryGetValue(map, out var list))
        {
            // Where weathers overlap, the first precipitating one in list order wins.
            for (var i = 0; i < list.Count; i++)
            {
                var weather = list[i];

                if (weather.IsPrecipitating && weather.CoversLocation(p))
                {
                    snow = weather.IsSnowing;
                    return true;
                }
            }
        }

        snow = false;
        return false;
    }
}
