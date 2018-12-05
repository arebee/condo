namespace condo
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Controls.Primitives;
    using System.Windows.Input;
    using System.Windows.Media;
    using ConsoleBuffer;

    public sealed class Screen : FrameworkElement, IRenderTarget, IScrollInfo
    {
        private ConsoleBuffer.Buffer buffer;
        public ConsoleBuffer.Buffer Buffer
        {
            get { return this.buffer; }
            set
            {
                if (this.buffer != null)
                {
                    this.Buffer.PropertyChanged -= this.OnBufferPropertyChanged;
                }

                this.buffer = value ?? throw new ArgumentNullException(nameof(value));
                this.Resize();
                this.buffer.PropertyChanged += this.OnBufferPropertyChanged;
                this.buffer.Palette = this.palette;
            }
        }

        private readonly XtermPalette palette = XtermPalette.Default;

        private VisualCollection children;
        private DpiScale dpiInfo;
        private readonly Typeface typeface;
        private readonly GlyphTypeface glyphTypeface;
        private readonly int fontSize = 16;
        private readonly double cellWidth, cellHeight;
        private readonly Point baselineOrigin;
        private int horizontalCells, verticalCells;
        private Character[,] characters;
        bool cursorInverted;
        private volatile int bufferUpdated;
        private int consoleBufferSize;
        private SolidBrushCache brushCache = new SolidBrushCache();

        private static readonly TimeSpan MaxRedrawFrequency = TimeSpan.FromMilliseconds(10); // slightly over 60fps
        private readonly Stopwatch redrawWatch = new Stopwatch();
        private static readonly TimeSpan BlinkFrequency = TimeSpan.FromMilliseconds(250);
        private readonly Stopwatch cursorBlinkWatch = new Stopwatch();

        /// <summary>
        /// Empty ctor for designer purposes at present. Probably don't use.
        /// </summary>
        public Screen() : this(new ConsoleBuffer.Buffer(80, 25))
        {
#if DEBUG
            for (var i = 0; i < 100; ++i)
            {
                var line = System.Text.Encoding.UTF8.GetBytes($"line {i}\r\n");
                this.Buffer.Append(line, line.Length);
            }
#endif
        }

        public Screen(ConsoleBuffer.Buffer buffer)
        {
            this.dpiInfo = VisualTreeHelper.GetDpi(this);
            this.children = new VisualCollection(this);
            this.typeface = new Typeface("Consolas");
            if (!this.typeface.TryGetGlyphTypeface(out this.glyphTypeface))
            {
                throw new InvalidOperationException("Could not get desired font.");
            }
            this.cellWidth = this.glyphTypeface.AdvanceWidths[' '] * this.fontSize; 
            this.cellHeight = this.glyphTypeface.Height * this.fontSize;
            this.baselineOrigin = new Point(0, this.glyphTypeface.Baseline * this.fontSize);

            this.redrawWatch.Start();
            this.cursorBlinkWatch.Start();

            CompositionTarget.Rendering += this.RenderFrame;
            this.MouseEnter += (sender, args) =>
            {
                args.MouseDevice.OverrideCursor = Cursors.IBeam;
            };
            this.MouseLeave += (sender, args) =>
            {
                args.MouseDevice.OverrideCursor = Cursors.Arrow;
            };

            this.Buffer = buffer;
        }

        private void OnBufferPropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == string.Empty)
            {
                // if we're scrolled to the bottom prior to redrawing (the conditional) indicate that we should render,
                // if we are not scrolled to the bottom we don't need to render unless the offset is changed by the
                // user.
                if (this.VerticalOffset == this.ExtentHeight - this.ViewportHeight)
                {
                    this.bufferUpdated = 1;
                }
            }
        }

        private void RenderFrame(object sender, EventArgs e)
        {
            if (this.redrawWatch.Elapsed >= MaxRedrawFrequency)
            {
                var shouldRedraw = (this.bufferUpdated != 0);
                this.redrawWatch.Restart();

                // when rendering we should update our view of the buffer size, and if we were previously scrolled
                // to the bottom ensure we stay that way after doing so.
                var bufferSize = this.Buffer.BufferSize;
                var activeScreen = this.VerticalOffset == this.ExtentHeight - this.ViewportHeight;
                shouldRedraw &= bufferSize != this.consoleBufferSize;
                this.consoleBufferSize = bufferSize;
                if (activeScreen)
                {
                    this.VerticalOffset = double.MaxValue;
                    var startLine = this.VerticalOffset;
                    this.Buffer.RenderFromLine(this, (int)startLine);

                    // blink the cursor if it's visible and the screen is active, otherwise ensure we don't do anything with it.
                    // this might be better off in redraw.
                    if (this.Buffer.CursorVisible && this.cursorBlinkWatch.Elapsed >= BlinkFrequency)
                    {
                        this.cursorBlinkWatch.Restart();
                        this.cursorInverted = this.Buffer.CursorBlink ? !this.cursorInverted : true;
                        shouldRedraw = true;
                    }
                    else
                    {
                        this.cursorInverted = false;
                    }

                    if (shouldRedraw) this.Redraw();
                }
                this.ScrollOwner?.ScrollToVerticalOffset(this.VerticalOffset);
            }
        }

        public void Close()
        {
            this.Buffer.PropertyChanged -= this.OnBufferPropertyChanged;
        }

        protected override int VisualChildrenCount => this.children.Count;

        protected override Visual GetVisualChild(int index)
        {
            return this.children[index];
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(this.cellWidth * this.horizontalCells, this.cellHeight * this.verticalCells);
        }

        private void Resize()
        {
            this.children.Clear();

            this.horizontalCells = this.Buffer.Width;
            this.verticalCells = this.Buffer.Height;
            this.characters = new Character[this.Buffer.Width, this.Buffer.Height];
            this.children.Add(new DrawingVisual { Offset = new Vector(0, 0) });

            this.Width = this.horizontalCells * this.cellWidth;
            this.Height = this.verticalCells * this.cellHeight;
            this.consoleBufferSize = this.Buffer.BufferSize;
        }

        public void RenderCharacter(Character c, int x, int y)
        {
            this.characters[x, y] = c;
        }

        private void Redraw()
        {
            var dv = this.children[0] as DrawingVisual;
            var glyphChars = new List<ushort>(this.Buffer.Width);
            var advanceWidths = new List<double>(this.Buffer.Width);

            using (var dc = dv.RenderOpen())
            {
                for (var y = 0; y < this.Buffer.Height; ++y)
                {
                    var runStart = 0; // cell where the current run of same colored characters began.
                    var x = 0;
                    var lineTop = y * this.cellHeight;
                    glyphChars.Clear();
                    advanceWidths.Clear();

                    while (x < this.Buffer.Width)
                    {
                        ++x;
                        (var fg, var bg) = this.GetCharacterRenderColors(x, y);
                        if (   x == this.Buffer.Width
                            || this.characters[runStart, y].Foreground != fg
                            || this.characters[runStart, y].Background != bg)
                        {
                            var ch = this.characters[runStart, y];
                            var backgroundBrush = this.brushCache.GetBrush(ch.Background.R, ch.Background.G, ch.Background.B);
                            var foregroundBrush = this.brushCache.GetBrush(ch.Foreground.R, ch.Foreground.G, ch.Foreground.B);

                            glyphChars.Clear();
                            advanceWidths.Clear();
                            for (var c = runStart; c < x; ++c)
                            {
                                if (!this.glyphTypeface.CharacterToGlyphMap.TryGetValue((char)this.characters[c, y].Glyph, out var glyphIndex))
                                {
                                    glyphIndex = 0;
                                }
                                glyphChars.Add(glyphIndex);
                                advanceWidths.Add(this.glyphTypeface.AdvanceWidths[glyphIndex] * this.fontSize);
                            }

                            var origin = new Point((runStart * this.cellWidth) + this.baselineOrigin.X, (y * this.cellHeight) + this.baselineOrigin.Y);
                            var gr = new GlyphRun(this.glyphTypeface, 0, false, this.fontSize, (float)this.dpiInfo.PixelsPerDip, new List<ushort>(glyphChars),
                                origin, new List<double>(advanceWidths), null, null, null, null, null, null);

                            dc.DrawRectangle(backgroundBrush, null, new Rect(runStart * this.cellWidth, lineTop, (x - runStart) * this.cellWidth, this.cellHeight));
                            dc.DrawGlyphRun(foregroundBrush, gr);

                            runStart = x;
                        }
                    }
                }
            }
        }

        private (Character.ColorInfo, Character.ColorInfo) GetCharacterRenderColors(int x, int y)
        {
            x = Math.Min(this.Buffer.Width - 1, x);
            (var f, var b) = (this.characters[x, y].Foreground, this.characters[x, y].Background);
            (var cursorX, var cursorY) = this.Buffer.CursorPosition;
            return this.cursorInverted && x == cursorX && y == cursorY ? (b, f) : (f, b);
        }

#region IScrollInfo
        public bool CanVerticallyScroll { get; set; }
        public bool CanHorizontallyScroll { get; set; }

        public double ExtentWidth => this.Buffer.Width;

        public double ExtentHeight => this.consoleBufferSize;

        public double ViewportWidth => this.Buffer.Width;

        public double ViewportHeight => this.Buffer.Height;

        public double HorizontalOffset => 0.0;

        private double verticalOffset;
        public double VerticalOffset
        {
            get
            {
                return this.verticalOffset;
            }
            set
            {
                var newValue = Math.Max(0, Math.Min(this.ExtentHeight - this.ViewportHeight, value));
                if (this.verticalOffset != newValue)
                {
                    this.verticalOffset = newValue;
                    this.bufferUpdated = 1;
                }
            }
        }

        public ScrollViewer ScrollOwner { get; set; }

        public void LineUp()
        {
            this.VerticalOffset -= 1;
        }

        public void LineDown()
        {
            this.VerticalOffset += 1;
        }

        public void LineLeft() { }

        public void LineRight() { }

        public void PageUp()
        {
            this.VerticalOffset -= this.Buffer.Height;
        }

        public void PageDown()
        {
            this.VerticalOffset += this.Buffer.Height;
        }

        public void PageLeft() { }

        public void PageRight() { }

        public void MouseWheelUp()
        {
            this.VerticalOffset -= 3;
        }

        public void MouseWheelDown()
        {
            this.VerticalOffset += 3;
        }

        public void MouseWheelLeft() { }

        public void MouseWheelRight() { }

        public void SetHorizontalOffset(double offset) { }

        public void SetVerticalOffset(double offset)
        {
            this.VerticalOffset = offset;
        }

        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            throw new NotImplementedException();
        }
#endregion
    }
}
