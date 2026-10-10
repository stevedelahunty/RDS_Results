using System.Windows.Media;

namespace RDS_Results;

static class Marks
{
    public static Geometry Runner { get; } = Freeze("M13.5,5.5c1.1,0,2-0.9,2-2s-0.9-2-2-2s-2,0.9-2,2S12.4,5.5,13.5,5.5z M9.8,8.9L7,23h2.1l1.8-8l2.1,2v6h2v-7.5l-2.1-2l0.6-3C14.8,12,16.8,13,19,13v-2c-1.9,0-3.5-1-4.3-2.4l-1-1.6c-0.4-0.6-1-1-1.7-1c-0.3,0-0.5,0.1-0.8,0.1L6,8.3V13h2V9.6L9.8,8.9z");
    public static Geometry Mountain { get; } = Freeze("M3,19 L10,5 L13,11 L16,6 L21,19 Z");
    public static Geometry Monitor { get; } = Freeze("F1 M2,3 H22 V16 H2 Z M4,5 H20 V14 H4 Z M10,17 H14 V18.5 H17 V20.5 H7 V18.5 H10 Z");
    public static Geometry Kiosk { get; } = Freeze("F1 M7,2 H17 V13 H7 Z M9,4 H15 V11 H9 Z M10.5,13 H13.5 V17 H16.5 V19 H7.5 V17 H10.5 Z");
    public static Geometry Printer { get; } = Freeze("F1 M7,2 H17 V6 H7 Z M3,7 H21 V16 H17 V13 H7 V16 H3 Z M8,15 H16 V22 H8 Z");
    public static Geometry Pin { get; } = Freeze("F1 M12,2C8.13,2 5,5.13 5,9c0,5.25 7,13 7,13s7,-7.75 7,-13c0,-3.87 -3.13,-7 -7,-7z M12,11.5A2.5,2.5 0 1,1 12,6.5A2.5,2.5 0 0,1 12,11.5z");
    public static Geometry Calendar { get; } = Freeze("M7,2 h2 v2 h6 V2 h2 v2 h2 v4 H5 V4 h2 z M5,10 h14 v10 H5 z");

    static Geometry Freeze(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
