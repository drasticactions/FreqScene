using System.Text.Json.Serialization;

namespace FreqScene;

[JsonConverter(typeof(JsonStringEnumConverter<DisplayMode>))]
public enum DisplayMode
{
    Window,

    Overlay,

    Wallpaper,
}
