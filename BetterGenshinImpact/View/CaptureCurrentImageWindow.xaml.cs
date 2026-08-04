using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace BetterGenshinImpact.View;

public partial class CaptureCurrentImageWindow
{
    private double _zoomFactor = 1.0;
    private const double ZoomStep = 0.1;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 10.0;

    private WriteableBitmap? _bitmap;
    private Point _lastPanPosition;
    private bool _isPanning;
    private bool _isAnalyzeMode;

    private double _originalImageWidth;
    private double _originalImageHeight;

    // 追踪平移偏移（视觉坐标系中的偏移）
    private double _panOffsetX;
    private double _panOffsetY;

    // 框选模式
    private bool _isSelectMode;
    private bool _isSelecting;
    private Point _selectStart;
    private Rectangle? _selectionRect;

    public CaptureCurrentImageWindow()
    {
        InitializeComponent();
    }

    public void SetImage(WriteableBitmap bitmap)
    {
        _bitmap = bitmap;
        _originalImageWidth = bitmap.PixelWidth;
        _originalImageHeight = bitmap.PixelHeight;
        DisplayImage.Source = bitmap;

        ImageCanvas.Width = bitmap.PixelWidth;
        ImageCanvas.Height = bitmap.PixelHeight;

        // 重置缩放和平移
        _zoomFactor = 1.0;
        _panOffsetX = 0;
        _panOffsetY = 0;
        ApplyTransform();

        // 居中图像
        CenterImage();

        ImageSizeText.Text = $"图像尺寸: {bitmap.PixelWidth} x {bitmap.PixelHeight}";
        UpdateStatus("拖动图像平移，滚轮缩放");
    }

    private void CenterImage()
    {
        if (_bitmap == null) return;
        // 计算初始居中偏移
        _panOffsetX = (ImageBorder.ActualWidth - _originalImageWidth * _zoomFactor) / 2;
        _panOffsetY = (ImageBorder.ActualHeight - _originalImageHeight * _zoomFactor) / 2;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        // 使用 RenderTransform 对 Image 应用缩放和平移
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(new ScaleTransform(_zoomFactor, _zoomFactor));
        transformGroup.Children.Add(new TranslateTransform(_panOffsetX, _panOffsetY));
        DisplayImage.RenderTransform = transformGroup;
    }

    /// <summary>
    /// 将鼠标在 Border 中的位置转换为图像原始坐标系中的位置
    /// </summary>
    private Point MouseToImagePosition(Point mouseBorderPos)
    {
        // 先减去平移偏移，再除以缩放因子
        double x = (mouseBorderPos.X - _panOffsetX) / _zoomFactor;
        double y = (mouseBorderPos.Y - _panOffsetY) / _zoomFactor;
        return new Point(x, y);
    }

    private void ImageBorder_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_bitmap == null) return;

        // 获取鼠标在 Border 中的位置
        var mouseBorderPos = e.GetPosition(ImageBorder);

        // 转换为图像原始坐标系中的位置
        var mouseImagePos = MouseToImagePosition(mouseBorderPos);

        // 检查鼠标是否在图像上（原始坐标）
        if (mouseImagePos.X < 0 || mouseImagePos.X >= _originalImageWidth ||
            mouseImagePos.Y < 0 || mouseImagePos.Y >= _originalImageHeight)
        {
            return;
        }

        // 更新缩放因子
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Delta > 0)
                _zoomFactor = Math.Min(MaxZoom, _zoomFactor + ZoomStep);
            else
                _zoomFactor = Math.Max(MinZoom, _zoomFactor - ZoomStep);
        }
        else
        {
            if (e.Delta > 0)
                _zoomFactor = Math.Min(MaxZoom, _zoomFactor + ZoomStep * 2);
            else
                _zoomFactor = Math.Max(MinZoom, _zoomFactor - ZoomStep * 2);
        }

        // 计算新的平移偏移，使鼠标指向图像上同一点
        // 公式：mouseBorder = panOffset + mouseImage * zoomFactor
        // 要保持同一点：newPanOffset = mouseBorder - mouseImage * newZoomFactor
        _panOffsetX = mouseBorderPos.X - mouseImagePos.X * _zoomFactor;
        _panOffsetY = mouseBorderPos.Y - mouseImagePos.Y * _zoomFactor;

        ApplyTransform();

        UpdateStatus($"缩放: {_zoomFactor:P0}");
        e.Handled = true;
    }

    private void ImageBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isAnalyzeMode)
        {
            // 分析模式：分析颜色
            var mouseBorderPos = e.GetPosition(ImageBorder);
            var mouseImagePos = MouseToImagePosition(mouseBorderPos);
            AnalyzeColor(mouseImagePos);
            e.Handled = true;
        }
        else if (_isSelectMode)
        {
            // 框选模式：开始框选
            _isSelecting = true;
            _selectStart = e.GetPosition(SelectionCanvas);
            CreateSelectionRect();
            SelectionCanvas.Visibility = Visibility.Visible;
            SelectionCanvas.CaptureMouse();
            e.Handled = true;
        }
        else
        {
            // 拖动模式：平移图像
            _isPanning = true;
            _lastPanPosition = e.GetPosition(ImageBorder);
            ImageBorder.CaptureMouse();
            ImageBorder.Cursor = Cursors.Hand;
            e.Handled = true;
        }
    }

    private void ImageBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isAnalyzeMode)
        {
            // 分析模式下显示颜色预览
            var mouseBorderPos = e.GetPosition(ImageBorder);
            var mouseImagePos = MouseToImagePosition(mouseBorderPos);
            ShowColorPreview(mouseImagePos);
        }
        else if (_isSelectMode && _isSelecting)
        {
            // 框选模式：更新选框
            UpdateSelectionRect(e.GetPosition(SelectionCanvas));
        }
        else if (_isPanning)
        {
            // 拖动平移
            var currentPos = e.GetPosition(ImageBorder);
            var deltaX = currentPos.X - _lastPanPosition.X;
            var deltaY = currentPos.Y - _lastPanPosition.Y;

            _panOffsetX += deltaX;
            _panOffsetY += deltaY;

            ApplyTransform();

            _lastPanPosition = currentPos;
        }
    }

    private void ImageBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSelecting)
        {
            _isSelecting = false;
            SelectionCanvas.ReleaseMouseCapture();
            UpdateSelectionRect(e.GetPosition(SelectionCanvas));
            UpdateStatus("框选完成，点击裁剪保存按钮保存");
            e.Handled = true;
        }
        else if (_isPanning)
        {
            _isPanning = false;
            ImageBorder.ReleaseMouseCapture();
            ImageBorder.Cursor = _isSelectMode ? Cursors.Cross : Cursors.Arrow;
            e.Handled = true;
        }
    }

    private void AnalyzeColor(Point pos)
    {
        if (_bitmap == null) return;

        // 检查点击位置是否在图像范围内
        if (pos.X < 0 || pos.X >= _bitmap.PixelWidth ||
            pos.Y < 0 || pos.Y >= _bitmap.PixelHeight)
        {
            return;
        }

        // 获取像素颜色
        int x = (int)pos.X;
        int y = (int)pos.Y;
        int stride = _bitmap.PixelWidth * 4;
        var pixels = new byte[stride * _bitmap.PixelHeight];
        _bitmap.CopyPixels(pixels, stride, 0);

        int i = (y * stride) + (x * 4);
        byte b = pixels[i];
        byte g = pixels[i + 1];
        byte r = pixels[i + 2];

        // RGB to HSV
        double rNorm = r / 255.0;
        double gNorm = g / 255.0;
        double bNorm = b / 255.0;

        double max = Math.Max(rNorm, Math.Max(gNorm, bNorm));
        double min = Math.Min(rNorm, Math.Min(gNorm, bNorm));
        double delta = max - min;

        double h = 0, s = 0, v = max;

        if (delta != 0)
        {
            if (max == rNorm)
                h = 60 * (((gNorm - bNorm) / delta) % 6);
            else if (max == gNorm)
                h = 60 * (((bNorm - rNorm) / delta) + 2);
            else
                h = 60 * (((rNorm - gNorm) / delta) + 4);

            if (h < 0) h += 360;
            s = (max == 0) ? 0 : delta / max;
        }

        int hDeg = (int)(h / 2);
        int sPct = (int)(s * 100);
        int vPct = (int)(v * 100);

        // 显示结果
        var result = new StringBuilder();
        result.AppendLine($"位置: ({x}, {y})");
        result.AppendLine();
        result.AppendLine($"RGB: ({r}, {g}, {b})");
        result.AppendLine($"  #{(r << 16 | g << 8 | b):X6}");
        result.AppendLine();
        result.AppendLine($"HSV (OpenCV):");
        result.AppendLine($"  H: {hDeg} (0-179)");
        result.AppendLine($"  S: {sPct}%");
        result.AppendLine($"  V: {vPct}%");
        result.AppendLine();
        result.AppendLine($"HSV (标准):");
        result.AppendLine($"  H: {h:F0}°");
        result.AppendLine($"  S: {sPct}%");
        result.AppendLine($"  V: {vPct}%");

        UpdateStatus($"位置: ({x}, {y}) RGB: ({r},{g},{b}) HSV: ({hDeg},{sPct}%,{vPct}%)");

        MessageBox.Show(result.ToString(), "颜色分析", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowColorPreview(Point pos)
    {
        if (_bitmap == null) return;

        if (pos.X < 0 || pos.X >= _bitmap.PixelWidth ||
            pos.Y < 0 || pos.Y >= _bitmap.PixelHeight)
        {
            UpdateStatus("分析模式 - 移动查看颜色预览");
            return;
        }

        int x = (int)pos.X;
        int y = (int)pos.Y;
        int stride = _bitmap.PixelWidth * 4;
        var pixels = new byte[stride * _bitmap.PixelHeight];
        _bitmap.CopyPixels(pixels, stride, 0);

        int i = (y * stride) + (x * 4);
        byte b = pixels[i];
        byte g = pixels[i + 1];
        byte r = pixels[i + 2];

        // RGB to HSV
        double rNorm = r / 255.0;
        double gNorm = g / 255.0;
        double bNorm = b / 255.0;

        double max = Math.Max(rNorm, Math.Max(gNorm, bNorm));
        double min = Math.Min(rNorm, Math.Min(gNorm, bNorm));
        double delta = max - min;

        double h = 0, s = 0, v = max;

        if (delta != 0)
        {
            if (max == rNorm)
                h = 60 * (((gNorm - bNorm) / delta) % 6);
            else if (max == gNorm)
                h = 60 * (((bNorm - rNorm) / delta) + 2);
            else
                h = 60 * (((rNorm - gNorm) / delta) + 4);

            if (h < 0) h += 360;
            s = (max == 0) ? 0 : delta / max;
        }

        int hDeg = (int)(h / 2);
        int sPct = (int)(s * 100);
        int vPct = (int)(v * 100);

        UpdateStatus($"分析模式 - 位置: ({x}, {y}) RGB: ({r},{g},{b}) HSV: ({hDeg},{sPct}%,{vPct}%)");
    }

    private void AnalyzeModeButton_Click(object sender, RoutedEventArgs e)
    {
        _isAnalyzeMode = true;
        AnalyzeModeButton.Visibility = Visibility.Collapsed;
        ExitAnalyzeButton.Visibility = Visibility.Visible;
        ImageBorder.Cursor = Cursors.Cross;
        UpdateStatus("分析模式 - 点击图像查看颜色");
    }

    private void SelectModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSelectMode)
        {
            // 退出框选模式
            _isSelectMode = false;
            SelectModeButton.Content = "框选模式";
            ExitSelection();
            ImageBorder.Cursor = Cursors.Arrow;
        }
        else
        {
            // 进入框选模式
            _isSelectMode = true;
            SelectModeButton.Content = "退出框选";
            ExitAnalyzeMode();
            ExitSelection();
            ImageBorder.Cursor = Cursors.Cross;
            UpdateStatus("框选模式：在图像上拖动框选区域");
        }
    }

    private void ExitAnalyzeMode()
    {
        if (_isAnalyzeMode)
        {
            _isAnalyzeMode = false;
            AnalyzeModeButton.Visibility = Visibility.Visible;
            ExitAnalyzeButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ExitSelection()
    {
        SelectionCanvas.Visibility = Visibility.Collapsed;
        SelectionCanvas.Children.Clear();
        _selectionRect = null;
        CropButton.IsEnabled = false;
        SaveMarkedButton.IsEnabled = false;
    }

    private void CreateSelectionRect()
    {
        SelectionCanvas.Children.Clear();
        _selectionRect = new Rectangle
        {
            Stroke = Brushes.Lime,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(50, 0, 255, 0)),
            Tag = new Rect()
        };
        SelectionCanvas.Children.Add(_selectionRect);
    }

    private void UpdateSelectionRect(Point currentPos)
    {
        if (_selectionRect == null) return;

        double x = Math.Min(_selectStart.X, currentPos.X);
        double y = Math.Min(_selectStart.Y, currentPos.Y);
        double width = Math.Abs(currentPos.X - _selectStart.X);
        double height = Math.Abs(currentPos.Y - _selectStart.Y);

        _selectionRect.Margin = new Thickness(x, y, 0, 0);
        _selectionRect.Width = Math.Max(1, width);
        _selectionRect.Height = Math.Max(1, height);

        // 保存选框在图像原始坐标系中的位置
        var topLeftImage = ScreenToImage(new Point(x, y));
        var bottomRightImage = ScreenToImage(new Point(x + width, y + height));

        var rectInImage = new Rect(
            topLeftImage.X,
            topLeftImage.Y,
            bottomRightImage.X - topLeftImage.X,
            bottomRightImage.Y - topLeftImage.Y);

        _selectionRect.Tag = rectInImage;

        // 启用裁剪按钮
        CropButton.IsEnabled = rectInImage.Width > 5 && rectInImage.Height > 5;
        SaveMarkedButton.IsEnabled = rectInImage.Width > 5 && rectInImage.Height > 5;

        UpdateStatus($"选框: ({rectInImage.X:F0},{rectInImage.Y:F0}) {rectInImage.Width:F0}x{rectInImage.Height:F0}");
    }

    /// <summary>
    /// 将 SelectionCanvas 屏幕坐标转换为图像原始坐标系中的坐标
    /// </summary>
    private Point ScreenToImage(Point screenPos)
    {
        // SelectionCanvas 的位置已经在 DisplayImage 的变换后空间中了
        // 需要考虑 DisplayImage 的 RenderTransform
        var imagePos = MouseToImagePosition(screenPos);
        return imagePos;
    }

    private void CropButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bitmap == null || _selectionRect == null) return;

        var rectInImage = (Rect)_selectionRect.Tag;

        // 确保选区在图像范围内
        int x = Math.Max(0, (int)rectInImage.X);
        int y = Math.Max(0, (int)rectInImage.Y);
        int width = Math.Min((int)rectInImage.Width, _bitmap.PixelWidth - x);
        int height = Math.Min((int)rectInImage.Height, _bitmap.PixelHeight - y);

        if (width <= 0 || height <= 0)
        {
            MessageBox.Show("选区无效", "裁剪失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            // 使用 CroppedBitmap 裁剪图像
            var croppedBitmap = new CroppedBitmap(_bitmap, new Int32Rect(x, y, width, height));

            // 保存到文件
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp",
                DefaultExt = ".png",
                FileName = $"{x}_{y}_{width}x{height}"
            };

            if (dialog.ShowDialog() == true)
            {
                BitmapEncoder encoder = dialog.FilterIndex switch
                {
                    1 => new PngBitmapEncoder(),
                    2 => new JpegBitmapEncoder(),
                    3 => new BmpBitmapEncoder(),
                    _ => new PngBitmapEncoder()
                };

                encoder.Frames.Add(BitmapFrame.Create(croppedBitmap));

                using var stream = File.Create(dialog.FileName);
                encoder.Save(stream);

                MessageBox.Show($"图片已保存到:\n{dialog.FileName}", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"裁剪保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bitmap == null) return;

        try
        {
            // 保存到文件
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp",
                DefaultExt = ".png",
                FileName = $"original_{_bitmap.PixelWidth}x{_bitmap.PixelHeight}"
            };

            if (dialog.ShowDialog() == true)
            {
                BitmapEncoder encoder = dialog.FilterIndex switch
                {
                    1 => new PngBitmapEncoder(),
                    2 => new JpegBitmapEncoder(),
                    3 => new BmpBitmapEncoder(),
                    _ => new PngBitmapEncoder()
                };

                encoder.Frames.Add(BitmapFrame.Create(_bitmap));

                using var stream = File.Create(dialog.FileName);
                encoder.Save(stream);

                MessageBox.Show($"图片已保存到:\n{dialog.FileName}", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveMarkedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bitmap == null || _selectionRect == null) return;
        if (_selectionRect.Tag is not Rect rectInImage) return;
        if (rectInImage.Width <= 5 || rectInImage.Height <= 5) return;

        // 钳制到图像边界
        int x = Math.Max(0, (int)rectInImage.X);
        int y = Math.Max(0, (int)rectInImage.Y);
        int width = Math.Min((int)rectInImage.Width, _bitmap.PixelWidth - x);
        int height = Math.Min((int)rectInImage.Height, _bitmap.PixelHeight - y);
        if (width <= 0 || height <= 0)
        {
            MessageBox.Show("选区无效", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var markedRect = new Rect(x, y, width, height);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));

                var boxPen = new Pen(Brushes.Lime, 3);
                var boxFill = new SolidColorBrush(Color.FromArgb(50, 0, 255, 0));
                dc.DrawRectangle(boxFill, boxPen, markedRect);

                var labelText = $"{x},{y} {width}x{height}";
                var ft = new FormattedText(
                    labelText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    12,
                    Brushes.White,
                    1.0);

                // 默认在框上方 20px；超出顶部时挪到框内 +2
                double labelY = markedRect.Y - 20 - ft.Height;
                if (labelY < 0)
                {
                    labelY = markedRect.Y + 2;
                }

                var labelBg = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
                var labelBgRect = new Rect(markedRect.X, labelY, ft.Width + 8, ft.Height);
                dc.DrawRectangle(labelBg, null, labelBgRect);
                dc.DrawText(ft, new Point(markedRect.X + 4, labelY));
            }

            var rtb = new RenderTargetBitmap(_bitmap.PixelWidth, _bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp",
                DefaultExt = ".png",
                FileName = $"{x}_{y}_{width}x{height}_marked"
            };

            if (dialog.ShowDialog() == true)
            {
                BitmapEncoder encoder = dialog.FilterIndex switch
                {
                    1 => new PngBitmapEncoder(),
                    2 => new JpegBitmapEncoder(),
                    3 => new BmpBitmapEncoder(),
                    _ => new PngBitmapEncoder()
                };

                encoder.Frames.Add(BitmapFrame.Create(rtb));

                using var stream = File.Create(dialog.FileName);
                encoder.Save(stream);

                MessageBox.Show($"图片已保存到:\n{dialog.FileName}", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExitAnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        _isAnalyzeMode = false;
        AnalyzeModeButton.Visibility = Visibility.Visible;
        ExitAnalyzeButton.Visibility = Visibility.Collapsed;
        ImageBorder.Cursor = Cursors.Arrow;
        UpdateStatus("拖动图像平移，滚轮缩放");
    }

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }
}