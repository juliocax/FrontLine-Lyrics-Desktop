using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml;

namespace FrontLineOverlay
{
    internal static class SvgGlyph
    {
        public static DrawingImage? TryLoadFile(string path, Color fill)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return null;
                return TryParse(File.ReadAllText(path), fill);
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "SvgGlyph.TryLoadFile");
                return null;
            }
        }

        public static DrawingImage? TryParse(string svg, Color fill)
        {
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                doc.LoadXml(svg);
                XmlElement? root = doc.DocumentElement;
                if (root == null) return null;

                Rect canvas = ReadViewBox(root);
                var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
                Collect(root, geometry);
                if (geometry.Children.Count == 0) return null;

                var group = new DrawingGroup();
                group.Children.Add(new GeometryDrawing(
                    Brushes.Transparent, null, new RectangleGeometry(canvas)));
                var brush = new SolidColorBrush(fill);
                if (brush.CanFreeze) brush.Freeze();
                group.Children.Add(new GeometryDrawing(brush, null, geometry));
                group.ClipGeometry = new RectangleGeometry(canvas);
                if (group.CanFreeze) group.Freeze();

                var image = new DrawingImage(group);
                if (image.CanFreeze) image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "SvgGlyph.TryParse");
                return null;
            }
        }

        private static Rect ReadViewBox(XmlElement root)
        {
            string vb = root.GetAttribute("viewBox");
            if (!string.IsNullOrWhiteSpace(vb))
            {
                string[] parts = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4
                    && TryNum(parts[0], out double x)
                    && TryNum(parts[1], out double y)
                    && TryNum(parts[2], out double w)
                    && TryNum(parts[3], out double h)
                    && w > 0 && h > 0)
                    return new Rect(x, y, w, h);
            }

            double width = AttrNum(root, "width", 24);
            double height = AttrNum(root, "height", 24);
            return new Rect(0, 0, Math.Max(1, width), Math.Max(1, height));
        }

        private static void Collect(XmlNode node, GeometryGroup sink)
        {
            if (node is XmlElement el)
            {
                string name = el.LocalName;
                if (name.Equals("path", StringComparison.OrdinalIgnoreCase))
                    AddParsed(sink, el.GetAttribute("d"));
                else if (name.Equals("polygon", StringComparison.OrdinalIgnoreCase)
                      || name.Equals("polyline", StringComparison.OrdinalIgnoreCase))
                    AddPolygon(sink, el.GetAttribute("points"), closed: name.Equals("polygon", StringComparison.OrdinalIgnoreCase));
            }

            foreach (XmlNode child in node.ChildNodes)
                Collect(child, sink);
        }

        private static void AddParsed(GeometryGroup sink, string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;
            try
            {
                Geometry g = Geometry.Parse(data);
                if (g.CanFreeze) g.Freeze();
                sink.Children.Add(g);
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "SvgGlyph.Path");
            }
        }

        private static void AddPolygon(GeometryGroup sink, string points, bool closed)
        {
            if (string.IsNullOrWhiteSpace(points)) return;
            string[] toks = points.Replace(',', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length < 4) return;
            var fig = new PathFigure { IsClosed = closed, IsFilled = true };
            bool start = true;
            for (int i = 0; i + 1 < toks.Length; i += 2)
            {
                if (!TryNum(toks[i], out double x) || !TryNum(toks[i + 1], out double y))
                    continue;
                var pt = new Point(x, y);
                if (start) { fig.StartPoint = pt; start = false; }
                else fig.Segments.Add(new LineSegment(pt, true));
            }
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            if (geo.CanFreeze) geo.Freeze();
            sink.Children.Add(geo);
        }

        private static double AttrNum(XmlElement el, string name, double fallback)
        {
            string raw = el.GetAttribute(name);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            raw = raw.Replace("px", "", StringComparison.OrdinalIgnoreCase).Trim();
            return TryNum(raw, out double n) ? n : fallback;
        }

        private static bool TryNum(string raw, out double n) =>
            double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n);
    }
}
