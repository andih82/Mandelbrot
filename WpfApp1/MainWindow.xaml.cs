using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Security.Cryptography.Xml;
using System.Threading.Tasks.Dataflow;

namespace MandelbrotViewer
{
    public partial class MainWindow : Window
    {
        private int MaxIterations = 10000;
        private WriteableBitmap fractalBitmap;
        private Color[] colorPalette = new Color[255];

        private double xmin = -2.5, xmax = 1.5, ymin = -2.0, ymax = 2.0;
        private Point? dragStart = null;

        private CancellationTokenSource _cancellationTokenSource;
        private CancellationToken _cancellationToken;

        private RenderMethod currentRenderMethod = RenderMethod.Normal;
        public enum RenderMethod
        {
            Normal,
            Parallel,
            TPL,
            TDF
        }


        public MainWindow()
        {
            InitializeComponent();
            GenerateColorPalette();
            RenderFractal();
        }

        private void GenerateColorPalette()
        {
            for (int i = 0; i < 255; i++)
            {
                double hue = (i * 360.0) / 255.0;
                colorPalette[i] = ColorFromHSV(hue, 1, 1);
            }
        }

        private Color ColorFromHSV(double hue, double saturation, double value)
        {
            hue %= 360;
            int hi = (int)(hue / 60) % 6;
            double f = (hue / 60) - Math.Floor(hue / 60);

            value = Math.Clamp(value, 0, 1);
            saturation = Math.Clamp(saturation, 0, 1);

            double v = value * 255;
            double p = v * (1 - saturation);
            double q = v * (1 - f * saturation);
            double t = v * (1 - (1 - f) * saturation);

            return hi switch
            {
                0 => Color.FromRgb((byte)v, (byte)t, (byte)p),
                1 => Color.FromRgb((byte)q, (byte)v, (byte)p),
                2 => Color.FromRgb((byte)p, (byte)v, (byte)t),
                3 => Color.FromRgb((byte)p, (byte)q, (byte)v),
                4 => Color.FromRgb((byte)t, (byte)p, (byte)v),
                _ => Color.FromRgb((byte)v, (byte)p, (byte)q),
            };
        }

        private async Task RenderFractal()
        {
            int width = (int)DrawCanvas.ActualWidth;
            int height = (int)DrawCanvas.ActualHeight;
            if (width == 0 || height == 0) return;

            fractalBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];

            // Track the start time of the render process
            DateTime startTime = DateTime.Now;

            // Update the status bar to show "Rendering..."
            DurationText.Text = "Rendering...";

            await Task.Run(() =>
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _cancellationToken = _cancellationTokenSource.Token;

                switch (currentRenderMethod)
                {
                    case RenderMethod.Normal:
                        RenderNormal(width, height, pixels, stride);
                        break;
                    case RenderMethod.Parallel:
                        RenderParallel(width, height, pixels, stride);
                        break;
                    case RenderMethod.TPL:
                        RenderTPL(width, height, pixels, stride);
                        break;
                    case RenderMethod.TDF:
                        RenderTDF(width, height, pixels, stride);
                        break;
                }
            }).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    DateTime endTime = DateTime.Now;
                    TimeSpan duration = endTime - startTime;

                    // Dauer im StatusBar TextBlock anzeigen
                    DurationText.Text = $"Duration: {duration.TotalSeconds:F2}s";
                });

            });



        }


        private void DrawLine(int y, int width, byte[] pixels, int stride)
        {
            Dispatcher.Invoke(() =>
            {
                // Aktualisiere das Bild im Image-Element, um nach jeder Zeile den Fortschritt zu zeigen
                fractalBitmap.WritePixels(new Int32Rect(0, 0, width, y + 1), pixels, stride, 0);
                FractalImage.Source = fractalBitmap;
            });
        }

        private void RenderTDF(int width, int height, byte[] pixels, int stride)
        {

            // ActionBlock zum Berechnen der Pixel
            var options = new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = _cancellationToken
            };

            // 1. TransformBlock: (x,y) -> (x,y,iteration count)
            var calculateIterationsBlock = new TransformBlock<(int x, int y), (int x, int y, double zx, double zy, int iterations)>(input =>
            {
                int x = input.x;
                int y = input.y;

                double a = xmin + (xmax - xmin) * x / width;
                double b = ymin + (ymax - ymin) * y / height;
                double zx = a, zy = b;
                int iter = 0;

                while (zx * zx + zy * zy <= 4 && iter < MaxIterations)
                {
                    double xtemp = zx * zx - zy * zy + a;
                    zy = 2 * zx * zy + b;
                    zx = xtemp;
                    iter++;
                }

                return (x, y,zx,zy, iter);

            },options);

            // 2. TransformBlock: (x,y,iterations) -> (x,y,Color)
            var colorizeBlock = new TransformBlock<(int x, int y, double zx, double zy ,int iterations), (int x, int y, Color)>(input =>
            {
                int x = input.x;
                int y = input.y;
                int iter = input.iterations;

                Color color;
                if (iter == MaxIterations)
                {
                    color = Colors.Black;
                }
                else
                {
                    double log_zn = Math.Log(input.zx * input.zx + input.zy * input.zy) / 2;
                    double nu = Math.Log(log_zn / Math.Log(2)) / Math.Log(2);
                    double smoothIter = iter + 1 - nu;
                    int colorIndex = (int)(smoothIter % 255);
                    color = colorPalette[colorIndex];
                }

                return (x, y, color);

            });

            // 3. BatchBlock: batch 1000 colored pixels together
            var batchBlock = new BatchBlock<(int x, int y, Color)>(10000, new GroupingDataflowBlockOptions
            {
                CancellationToken = _cancellationToken
            });

            // 4. ActionBlock: render 1000 pixels
            var progress = 0;
            var renderBlock = new ActionBlock<(int x, int y, Color color)[]>(batch =>
            {
                Dispatcher.Invoke(() =>
                {
                    foreach (var pixel in batch)
                    {
                        int index = (pixel.y * stride) + (pixel.x * 4);
                        pixels[index + 0] = pixel.color.B;
                        pixels[index + 1] = pixel.color.G;
                        pixels[index + 2] = pixel.color.R;
                        pixels[index + 3] = 255; // Alpha auf 255 setzen
                    }

                    // Update image
                    fractalBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                    FractalImage.Source = fractalBitmap;
                    UpdateProgress(progress += batch.Length, width * height);
                    
                });
            });


            // Wire the blocks
            calculateIterationsBlock.LinkTo(colorizeBlock, new DataflowLinkOptions { PropagateCompletion = true });
            colorizeBlock.LinkTo(batchBlock, new DataflowLinkOptions { PropagateCompletion = true });
            batchBlock.LinkTo(renderBlock, new DataflowLinkOptions { PropagateCompletion = true });

            // Post all pixels into the pipeline
            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    if (_cancellationToken.IsCancellationRequested)
                        break;

                    calculateIterationsBlock.Post((x, y));
                }
            });

            // Signal completion
            calculateIterationsBlock.Complete();

            // Wait for the full pipeline to finish
            renderBlock.Completion.Wait();

        }

        private async void RenderNormal(int width, int height, byte[] pixels, int stride)
        {
            // Hier kommt der normale (sequentielle) Code für die Berechnung des Mandelbrots
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (_cancellationToken.IsCancellationRequested)
                        return;
                    calculateFractal(x, y, width, height, pixels, stride);
                }
                DrawLine(y, width, pixels, stride);
                UpdateProgress(y + 1, height);
            }
        }

        private void RenderParallel(int width, int height, byte[] pixels, int stride)
        {
            // Hier wird die parallele Berechnung verwendet
            int progrss = 0;
            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    if (_cancellationToken.IsCancellationRequested)
                        return;
                    calculateFractal(x, y, width, height, pixels, stride);
                }
                DrawLine(y, width, pixels, stride);
                UpdateProgress(++progrss, height);
            });
        }

        private void RenderTPL(int width, int height, byte[] pixels, int stride)
        {
            int progrss = 0;
            // Hier wird die TPL (Task Parallel Library) verwendet
            Task.WhenAll(
                ParallelEnumerable.Range(0, height).Select(y =>
                {
                    return Task.Run(() =>
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (_cancellationToken.IsCancellationRequested)
                                return;
                            calculateFractal(x, y, width, height, pixels, stride);
                        }
                        DrawLine(y, width, pixels, stride);
                        UpdateProgress(++progrss, height);
                    });
                })
            ).Wait();
        }

        private void calculateFractal(int x, int y, int width, int height, byte[] pixels, int stride)
        {
            double a = xmin + (xmax - xmin) * x / width;
            double b = ymin + (ymax - ymin) * y / height;
            double zx = a, zy = b;
            int iter = 0;

            while (zx * zx + zy * zy <= 4 && iter < MaxIterations)
            {
                double xtemp = zx * zx - zy * zy + a;
                zy = 2 * zx * zy + b;
                zx = xtemp;
                iter++;
            }

            Color color;
            if (iter == MaxIterations)
            {
                color = Colors.Black;
            }
            else
            {
                double log_zn = Math.Log(zx * zx + zy * zy) / 2;
                double nu = Math.Log(log_zn / Math.Log(2)) / Math.Log(2);
                double smoothIter = iter + 1 - nu;
                int colorIndex = (int)(smoothIter % 255);
                color = colorPalette[colorIndex];
            }

            int index = (y * stride) + (x * 4);
            pixels[index + 0] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = 255;
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CancelCalculation();
            dragStart = e.GetPosition(DrawCanvas);
            SelectionRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(SelectionRect, dragStart.Value.X);
            Canvas.SetTop(SelectionRect, dragStart.Value.Y);
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragStart.HasValue)
            {
                Point pos = e.GetPosition(DrawCanvas);
                double x = Math.Min(pos.X, dragStart.Value.X);
                double y = Math.Min(pos.Y, dragStart.Value.Y);
                double w = Math.Abs(pos.X - dragStart.Value.X);
                double h = Math.Abs(pos.Y - dragStart.Value.Y);

                Canvas.SetLeft(SelectionRect, x);
                Canvas.SetTop(SelectionRect, y);
                SelectionRect.Width = w;
                SelectionRect.Height = h;
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!dragStart.HasValue)
                return;

            Point end = e.GetPosition(DrawCanvas);
            double x0 = Math.Min(dragStart.Value.X, end.X);
            double y0 = Math.Min(dragStart.Value.Y, end.Y);
            double x1 = Math.Max(dragStart.Value.X, end.X);
            double y1 = Math.Max(dragStart.Value.Y, end.Y);

            double nx0 = xmin + (xmax - xmin) * x0 / DrawCanvas.ActualWidth;
            double nx1 = xmin + (xmax - xmin) * x1 / DrawCanvas.ActualWidth;
            double ny0 = ymin + (ymax - ymin) * y0 / DrawCanvas.ActualHeight;
            double ny1 = ymin + (ymax - ymin) * y1 / DrawCanvas.ActualHeight;

            xmin = nx0;
            xmax = nx1;
            ymin = ny0;
            ymax = ny1;

            dragStart = null;
            SelectionRect.Visibility = Visibility.Collapsed;
            RenderFractal();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            CancelCalculation();
            double move = (xmax - xmin) * 0.1;
            switch (e.Key)
            {
                case Key.Left: xmin -= move; xmax -= move; break;
                case Key.Right: xmin += move; xmax += move; break;
                case Key.Up: ymin -= move; ymax -= move; break;
                case Key.Down: ymin += move; ymax += move; break;
                case Key.R:
                    xmin = -2.5; xmax = 1.5;
                    ymin = -2.0; ymax = 2.0;
                    break;
                default: return;
            }
            RenderFractal();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            CancelCalculation();
            RenderFractal();
        }

        private void SetIterations_Click(object sender, RoutedEventArgs e)
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter max iterations (e.g. 1000 - 100000):",
                "Set Iterations",
                MaxIterations.ToString()
            );

            if (int.TryParse(input, out int newVal) && newVal >= 10 && newVal <= 1000000)
            {
                MaxIterations = newVal;
                CancelCalculation();
                RenderFractal();
            }
            else
            {
                MessageBox.Show("Please enter a valid number between 10 and 1,000,000.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }



        private void RenderMethod_Click(object sender, RoutedEventArgs e)
        {
            // Das gewählte Render-Verfahren aus dem Tag des ausgewählten Menüpunktes holen
            if (sender is MenuItem menuItem && menuItem.Tag is string renderMethodName)
            {
                currentRenderMethod = Enum.Parse<RenderMethod>(renderMethodName);
                RenderMethode.Header = $"Render {currentRenderMethod}";
            }

            // Fraktal neu rendern mit der neuen Methode
            CancelCalculation();
            RenderFractal();
        }

        private async void UpdateProgress(int progress, int total)
        {
            // Berechne den Fortschritt als Prozentsatz
            double percentage = (double)progress / total * 100;
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = percentage;
            });
            
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CancelCalculation();
        }

        private void CancelCalculation()
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }
        }

    }
}
