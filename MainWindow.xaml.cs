using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Hikari;

public partial class MainWindow : Window
{
    Frame? frame;
    Peak? peak;
    Division? result;
    string? inputPath;
    int adjustment;
    bool busy, showingResult, selecting;
    Point? dragStart;
    double zoom = 1;
    Point viewCenter = new(0.5,0.5);
    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public MainWindow()
    {
        InitializeComponent();
        Refresh();
        Loaded += async (_,_) => {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) await OpenImage(args[1]);
        };
        PreviewKeyDown += (_,e) => {
            if (e.Key == Key.Escape && selecting) CancelSelection();
        };
    }

    void Refresh()
    {
        OpenButton.IsEnabled = !busy;
        OriginalButton.IsEnabled = !busy && frame != null && (showingResult || selecting);
        ResultButton.IsEnabled = !busy && result != null && (!showingResult || selecting);
        RegionButton.IsEnabled = !busy && frame != null;
        RegionButton.Content = selecting ? "範囲指定をキャンセル" : "目の範囲を指定…";
        DarkerButton.IsEnabled = !busy && !selecting && peak != null && adjustment > -100;
        LighterButton.IsEnabled = !busy && !selecting && peak != null && adjustment < 100;
        RestoreButton.IsEnabled = !busy && !selecting && peak != null && adjustment != 0;
        SaveButton.IsEnabled = !busy && !selecting && result != null;
        OverwriteButton.IsEnabled = SaveButton.IsEnabled;
        Preview.Source = showingResult && result != null ? result.Preview : frame?.Bitmap;
        Preview.Cursor = selecting || busy ? null : Cursors.Hand;
        Stage.Cursor = selecting ? Cursors.Cross : Cursors.Arrow;
        EmptyHint.Visibility = frame == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateView();
    }

    async Task RunBusy(Func<Task> work)
    {
        if (busy) return;
        busy = true;
        Refresh();
        try { await work(); }
        catch (Exception ex) { Status.Text = $"処理できませんでした。{ex.Message}"; }
        finally { busy = false; Refresh(); }
    }

    async Task OpenImage(string path)
    {
        await RunBusy(async () => {
            CancelSelection();
            Status.Text = "画像を読み込み、目のハイライトを探しています…";
            var loaded = await Task.Run(() => Frame.Load(path));
            var detection = await Task.Run(() => HighlightDetector.Find(loaded));
            frame = loaded;
            zoom = 1;
            viewCenter = new(0.5,0.5);
            inputPath = Path.GetFullPath(path);
            peak = detection?.Peak;
            result = null;
            adjustment = 0;
            showingResult = false;
            Title = $"{Path.GetFileName(path)}  |  光採り";
            Dimensions.Text = $"出力サイズ  {frame.Width:N0} × {frame.Height:N0} px";
            DetectLabel.Text = peak == null ? "候補を検出できませんでした。目の範囲を指定してください。" : "左右のハイライト候補から最も明るい画素を採用しました。";
            UpdateSample();
            if (peak != null) await RenderResult();
            else Status.Text = "「目の範囲を指定」で、ハイライトを囲んでください。";
        });
    }

    void UpdateSample()
    {
        Detail.Source = null;
        DetailMark.Visibility = Visibility.Collapsed;
        Coordinates.Text = "採色位置  —";
        ColorText.Text = "—";
        AdjustmentText.Text = "調整量  0%";
        Swatch.Background = Brushes.Transparent;
        if (frame == null || peak == null) return;
        int radius = Math.Clamp(Math.Min(frame.Width,frame.Height)/33,8,140);
        int rx = Math.Min(radius,Math.Min(peak.X,frame.Width-1-peak.X));
        int ry = Math.Min(radius,Math.Min(peak.Y,frame.Height-1-peak.Y));
        Detail.Source = new CroppedBitmap(frame.Bitmap,new Int32Rect(peak.X-rx,peak.Y-ry,2*rx+1,2*ry+1));
        DetailMark.Visibility = Visibility.Visible;
        Coordinates.Text = $"X {peak.X}   Y {peak.Y}     検出色 {Hex(peak.Color.Display)}";
    }

    async Task RenderResult()
    {
        if (frame == null || peak == null) return;
        var divisor = peak.Color.Adjust(adjustment);
        Status.Text = "除算画像を生成しています…";
        result = null;
        result = await Task.Run(() => frame.Divide(divisor));
        Swatch.Background = new SolidColorBrush(divisor.Display);
        ColorText.Text = Hex(divisor.Display);
        AdjustmentText.Text = $"調整量  {adjustment:+0;-0;0}%";
        showingResult = true;
        Status.Text = $"{Path.GetFileName(inputPath)}　 ·　 除算結果";
    }

    async void OpenClick(object sender,RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter="画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|すべてのファイル|*.*" };
        if (dialog.ShowDialog(this) == true) await OpenImage(dialog.FileName);
    }
    void OnDragOver(object sender,DragEventArgs e)
    {
        e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    async void OnDrop(object sender,DragEventArgs e)
    {
        if (!busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) await OpenImage(paths[0]);
    }
    void OriginalClick(object sender,RoutedEventArgs e) { CancelSelection(); showingResult = false; Status.Text = $"{Path.GetFileName(inputPath)}　 ·　 元画像"; Refresh(); }
    void ResultClick(object sender,RoutedEventArgs e) { CancelSelection(); showingResult = true; Status.Text = $"{Path.GetFileName(inputPath)}　 ·　 除算結果"; Refresh(); }
    async Task AdjustBy(int amount) => await RunBusy(async () => {
        adjustment = Math.Clamp(adjustment+amount,-100,100);
        await RenderResult();
    });
    async void DarkerClick(object sender,RoutedEventArgs e) => await AdjustBy(-5);
    async void LighterClick(object sender,RoutedEventArgs e) => await AdjustBy(5);
    async void RestoreClick(object sender,RoutedEventArgs e) => await AdjustBy(-adjustment);

    Rect ImageBounds()
    {
        if (frame == null) return Rect.Empty;
        double scale = Math.Min(Stage.ActualWidth/frame.Width,Stage.ActualHeight/frame.Height)*zoom;
        double w = frame.Width*scale, h = frame.Height*scale;
        static double Origin(double viewport,double size,double center)
        {
            // 端まで移動しても、画像をつかみ直せる幅を残す。
            double grip = Math.Min(24,Math.Min(viewport,size)/2);
            return Math.Clamp(viewport/2-center*size,grip-size,viewport-grip);
        }
        return new Rect(Origin(Stage.ActualWidth,w,viewCenter.X),Origin(Stage.ActualHeight,h,viewCenter.Y),w,h);
    }
    void ZoomWheel(object sender,MouseWheelEventArgs e)
    {
        if (frame == null || dragStart != null) return;
        var before = ImageBounds();
        var cursor = e.GetPosition(Stage);
        if (!before.Contains(cursor) || before.Width == 0 || before.Height == 0) return;
        e.Handled = true;
        double next = Math.Clamp(zoom*Math.Pow(1.25,e.Delta/120.0),0.25,32);
        if (next == zoom) return;
        double ratio = next/zoom;
        viewCenter = new Point(
            (cursor.X-before.Left)/before.Width+(Stage.ActualWidth/2-cursor.X)/(before.Width*ratio),
            (cursor.Y-before.Top)/before.Height+(Stage.ActualHeight/2-cursor.Y)/(before.Height*ratio));
        zoom = next;
        UpdateView();
    }
    void FitClick(object sender,RoutedEventArgs e)
    {
        zoom = 1;
        viewCenter = new(0.5,0.5);
        UpdateView();
    }
    void StageChanged(object sender,SizeChangedEventArgs e) => UpdateView();
    void UpdateView()
    {
        if (Preview == null || Mark == null) return;
        FitButton.IsEnabled = frame != null && dragStart == null;
        ZoomLabel.Text = frame == null ? "" : $"{zoom:P0}";
        if (frame != null)
        {
            var rect = ImageBounds();
            Canvas.SetLeft(Preview,rect.Left);
            Canvas.SetTop(Preview,rect.Top);
            Preview.Width = rect.Width;
            Preview.Height = rect.Height;
        }
        UpdateMark();
    }
    void UpdateMark()
    {
        if (Mark == null) return;
        Mark.Visibility = !showingResult && !selecting && peak != null ? Visibility.Visible : Visibility.Collapsed;
        if (peak == null || frame == null) return;
        var rect = ImageBounds();
        Canvas.SetLeft(Mark,rect.Left+(peak.X+0.5)*rect.Width/frame.Width-11);
        Canvas.SetTop(Mark,rect.Top+(peak.Y+0.5)*rect.Height/frame.Height-11);
    }
    void CancelSelection()
    {
        selecting = false;
        dragStart = null;
        Stage.ReleaseMouseCapture();
        Region.Visibility = Visibility.Collapsed;
        Status.Text = Path.GetFileName(inputPath) ?? "";
        Refresh();
    }
    void RegionClick(object sender,RoutedEventArgs e)
    {
        if (selecting) { CancelSelection(); return; }
        selecting = true;
        showingResult = false;
        Status.Text = "ドラッグでハイライトを囲んでください。クリックすると1画素を採色します。";
        Refresh();
    }
    void StartDrag(object sender,MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Stage);
        if (busy || !ImageBounds().Contains(p) || !Stage.CaptureMouse()) return;
        dragStart = p;
        Stage.Cursor = selecting ? Cursors.Cross : Cursors.Hand;
        FitButton.IsEnabled = false;
        MoveDrag(sender,e);
        e.Handled = true;
    }
    void DragCaptureLost(object sender,MouseEventArgs e)
    {
        dragStart = null;
        Region.Visibility = Visibility.Collapsed;
        Stage.Cursor = selecting ? Cursors.Cross : Cursors.Arrow;
        FitButton.IsEnabled = frame != null;
    }
    Rect SelectedBounds(Point p)
    {
        var r = new Rect(dragStart!.Value,p);
        r.Intersect(ImageBounds());
        return r;
    }
    void MoveDrag(object sender,MouseEventArgs e)
    {
        if (dragStart == null) return;
        var p = e.GetPosition(Stage);
        if (!selecting)
        {
            var bounds = ImageBounds();
            var delta = p-dragStart.Value;
            if (bounds.Width > 0 && bounds.Height > 0)
                viewCenter = new Point((Stage.ActualWidth/2-bounds.Left-delta.X)/bounds.Width,
                    (Stage.ActualHeight/2-bounds.Top-delta.Y)/bounds.Height);
            dragStart = p;
            UpdateView();
            return;
        }
        var r = SelectedBounds(p);
        Canvas.SetLeft(Region,r.X); Canvas.SetTop(Region,r.Y);
        Region.Width = r.Width; Region.Height = r.Height;
        Region.Visibility = Visibility.Visible;
    }
    async void EndDrag(object sender,MouseButtonEventArgs e)
    {
        if (dragStart == null || frame == null) return;
        e.Handled = true;
        if (!selecting)
        {
            MoveDrag(sender,e);
            Stage.ReleaseMouseCapture();
            return;
        }
        var r = SelectedBounds(e.GetPosition(Stage));
        var bounds = ImageBounds();
        int X(double x) => Math.Clamp((int)((x-bounds.X)/bounds.Width*frame.Width),0,frame.Width-1);
        int Y(double y) => Math.Clamp((int)((y-bounds.Y)/bounds.Height*frame.Height),0,frame.Height-1);
        int x0=X(r.Left), x1=X(r.Right), y0=Y(r.Top), y1=Y(r.Bottom);
        CancelSelection();
        await RunBusy(async () => {
            var sample = await Task.Run(() => HighlightDetector.InRegion(frame,x0,y0,x1,y1));
            if (sample == null) { Status.Text = "指定範囲は透明です。目の範囲を指定し直してください。"; return; }
            peak = sample; adjustment = 0;
            DetectLabel.Text = "指定した範囲の最も明るい画素を採用しました。";
            UpdateSample();
            await RenderResult();
        });
    }
    async void OverwriteClick(object sender,RoutedEventArgs e)
    {
        if (inputPath != null) await SaveResult(inputPath);
    }

    async void SaveClick(object sender,RoutedEventArgs e)
    {
        if (result == null || inputPath == null) return;
        var dialog = new SaveFileDialog { Filter="PNG 画像|*.png", DefaultExt=".png",
            InitialDirectory=Path.GetDirectoryName(inputPath), FileName=Path.GetFileNameWithoutExtension(inputPath)+"_除算.png" };
        if (dialog.ShowDialog(this) != true) return;
        await SaveResult(dialog.FileName);
    }

    async Task SaveResult(string path)
    {
        var output = result;
        if (output == null) return;
        bool overwrite = string.Equals(inputPath,Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase);
        await RunBusy(async () => {
            await Task.Run(() => WriteImage(path,output.ForSave(overwrite),overwrite));
            Status.Text = overwrite ? $"上書き保存しました　{path}" : $"保存しました　{path}";
        });
    }
    internal static void WriteImage(string path,BitmapSource image,bool overwrite)
    {
        string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,Path.GetRandomFileName());
        var tempFile = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        try
        {
            using (tempFile)
            {
                if (overwrite)
                {
                    using var source = ImageInput.OpenRead(path);
                    var decoder = BitmapDecoder.Create(source,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnDemand);
                    BitmapEncoder encoder = decoder switch {
                        PngBitmapDecoder => new PngBitmapEncoder(),
                        JpegBitmapDecoder => new JpegBitmapEncoder { QualityLevel=95 },
                        BmpBitmapDecoder => new BmpBitmapEncoder(),
                        TiffBitmapDecoder => new TiffBitmapEncoder(),
                        _ => throw new NotSupportedException("この形式は上書き保存に対応していません。「PNG に名前を付けて保存」を使ってください。")
                    };
                    var original = decoder.Frames[0];
                    encoder.Frames.Add(BitmapFrame.Create(image,null,original.Metadata as BitmapMetadata,original.ColorContexts));
                    foreach (var page in decoder.Frames.Skip(1)) encoder.Frames.Add(page);
                    encoder.Save(tempFile);
                }
                else
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(tempFile);
                }
            }
            if (overwrite) File.Replace(temp,path,null);
            else File.Move(temp,path,true);
        }
        finally { File.Delete(temp); }
    }
}
