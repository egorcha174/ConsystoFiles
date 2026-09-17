// Consysto fork: macOS-style window buttons.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;
using WinRT;

namespace Files.App.MacStyle
{
	/// <summary>
	/// Close, minimize and zoom as ordinary XAML buttons at the start of the title bar.
	/// </summary>
	/// <remarks>
	/// The buttons lie outside the caption region, so clicks reach XAML and the buttons act on the window themselves.
	/// Custom caption buttons through non-client hit testing (NonClientRegionKind.Close/Minimize/Maximize) did not react
	/// to clicks under Windows App SDK 2.4, and the system Snap Layouts flyout cannot be opened for them; resting the
	/// pointer on zoom opens an arrangement menu instead, the way macOS does.
	/// </remarks>
	public sealed partial class TrafficLights : UserControl
	{
		private static readonly TimeSpan ArrangeMenuDelay = TimeSpan.FromMilliseconds(600);

		private readonly DispatcherQueueTimer arrangeMenuTimer;
		private readonly (Ellipse Light, Brush Fill, Brush Stroke)[] lights;
		private bool isWindowActive = true;
		private bool isPointerOver;

		public TrafficLights()
		{
			InitializeComponent();

			// The colors come from the XAML; remember them to switch between colored and grey.
			lights =
			[
				(CloseFill, CloseFill.Fill, CloseFill.Stroke),
				(MinimizeFill, MinimizeFill.Fill, MinimizeFill.Stroke),
				(ZoomFill, ZoomFill.Fill, ZoomFill.Stroke),
			];

			arrangeMenuTimer = DispatcherQueue.CreateTimer();
			arrangeMenuTimer.Interval = ArrangeMenuDelay;
			arrangeMenuTimer.IsRepeating = false;
			arrangeMenuTimer.Tick += (_, _) => FlyoutBase.ShowAttachedFlyout(ZoomButton);

			Loaded += TrafficLights_Loaded;
			Unloaded += TrafficLights_Unloaded;
		}

		private static MainWindow Window => MainWindow.Instance;

		private void TrafficLights_Loaded(object sender, RoutedEventArgs e)
		{
			Window.Activated += Window_Activated;
			ActualThemeChanged += TrafficLights_ActualThemeChanged;
			UpdateVisuals();
		}

		private void TrafficLights_Unloaded(object sender, RoutedEventArgs e)
		{
			arrangeMenuTimer.Stop();
			Window.Activated -= Window_Activated;
			ActualThemeChanged -= TrafficLights_ActualThemeChanged;
		}

		private void TrafficLights_ActualThemeChanged(FrameworkElement sender, object args)
			=> UpdateVisuals();

		private void Window_Activated(object sender, WindowActivatedEventArgs args)
		{
			isWindowActive = args.WindowActivationState is not WindowActivationState.Deactivated;
			UpdateVisuals();
		}

		private void Lights_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			isPointerOver = true;
			UpdateVisuals();
		}

		private void Lights_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			isPointerOver = false;
			UpdateVisuals();
		}

		// Colored while the window is active or the pointer is over the buttons, grey otherwise; the glyphs show on hover.
		private void UpdateVisuals()
		{
			bool colored = isWindowActive || isPointerOver;
			bool dark = ActualTheme == ElementTheme.Dark;
			var inactiveFill = new SolidColorBrush(dark ? Color.FromArgb(255, 0x4A, 0x4A, 0x4C) : Color.FromArgb(255, 0xD9, 0xD9, 0xD9));
			var inactiveStroke = new SolidColorBrush(dark ? Color.FromArgb(255, 0x5C, 0x5C, 0x5E) : Color.FromArgb(255, 0xC6, 0xC6, 0xC6));

			foreach (var (light, fill, stroke) in lights)
			{
				light.Fill = colored ? fill : inactiveFill;
				light.Stroke = colored ? stroke : inactiveStroke;
			}

			double glyphOpacity = isPointerOver ? 1 : 0;
			CloseGlyph.Opacity = glyphOpacity;
			MinimizeGlyph.Opacity = glyphOpacity;
			ZoomGlyph.Opacity = glyphOpacity;
		}

		// Window.Closed runs App.Window_Closed, the same path as the system close button (session save, background mode).
		private void CloseButton_Click(object sender, RoutedEventArgs e)
			=> Window.Close();

		[DynamicWindowsRuntimeCast(typeof(OverlappedPresenter))]
		private void MinimizeButton_Click(object sender, RoutedEventArgs e)
		{
			if (Window.AppWindow.Presenter is OverlappedPresenter presenter)
				presenter.Minimize();
		}

		[DynamicWindowsRuntimeCast(typeof(OverlappedPresenter))]
		private void ZoomButton_Click(object sender, RoutedEventArgs e)
		{
			arrangeMenuTimer.Stop();
			if (Window.AppWindow.Presenter is not OverlappedPresenter presenter)
				return;

			if (presenter.State == OverlappedPresenterState.Maximized)
				presenter.Restore();
			else
				presenter.Maximize();
		}

		private void ZoomButton_PointerEntered(object sender, PointerRoutedEventArgs e)
			=> arrangeMenuTimer.Start();

		private void ZoomButton_PointerExited(object sender, PointerRoutedEventArgs e)
			=> arrangeMenuTimer.Stop();

		private void ZoomButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
		{
			arrangeMenuTimer.Stop();
			FlyoutBase.ShowAttachedFlyout(ZoomButton);
			e.Handled = true;
		}

		[DynamicWindowsRuntimeCast(typeof(OverlappedPresenter))]
		[DynamicWindowsRuntimeCast(typeof(MenuFlyoutItem))]
		private void ArrangeMenuItem_Click(object sender, RoutedEventArgs e)
		{
			if (sender is not MenuFlyoutItem { Tag: string arrangement })
				return;

			var appWindow = Window.AppWindow;
			if (appWindow.Presenter is not OverlappedPresenter presenter)
				return;

			if (arrangement == "Fill")
			{
				presenter.Maximize();
				return;
			}

			if (presenter.State == OverlappedPresenterState.Maximized)
				presenter.Restore();

			// Physical pixels of the monitor the window is on, without the taskbar.
			var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
			int width = Math.Min(appWindow.Size.Width, work.Width);
			int height = Math.Min(appWindow.Size.Height, work.Height);
			RectInt32 target = arrangement switch
			{
				"Left" => new(work.X, work.Y, work.Width / 2, work.Height),
				"Right" => new(work.X + work.Width / 2, work.Y, work.Width - work.Width / 2, work.Height),
				"Top" => new(work.X, work.Y, work.Width, work.Height / 2),
				"Bottom" => new(work.X, work.Y + work.Height / 2, work.Width, work.Height - work.Height / 2),
				_ => new(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height),
			};
			appWindow.MoveAndResize(target);
		}
	}
}
