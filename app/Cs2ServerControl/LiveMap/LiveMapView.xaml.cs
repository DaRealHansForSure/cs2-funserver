using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Cs2ServerControl.Services;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.Wpf.SharpDX;
using SharpDX;
using MediaColor = System.Windows.Media.Color;
using Point = System.Windows.Point;
using MeshGeometry3D = HelixToolkit.SharpDX.Core.MeshGeometry3D;
using Matrix = SharpDX.Matrix;
using TextInfo = HelixToolkit.SharpDX.Core.TextInfo;

namespace Cs2ServerControl.LiveMap;

/// <summary>
/// View helper for the Helix viewport: runs the frame loop, moves the camera
/// from the rig, turns mouse and keyboard input into rig actions and keeps
/// the player markers in the scene. Only view plumbing lives here; what is
/// shown comes from LiveMapViewModel.
/// </summary>
public partial class LiveMapView : UserControl {
    private const double DragThreshold = 4;
    private const double PickRadiusPixels = 16;
    private const float KeyboardOrbitDegreesPerSecond = 90f;
    private const float KeyboardTiltDegreesPerSecond = 45f;
    private const float KeyboardZoomPerSecond = 1.2f;

    private readonly Dictionary<int, NameLabel> _labels = [];
    private readonly HashSet<Key> _heldKeys = [];
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private readonly Stopwatch _overlayClock = Stopwatch.StartNew();

    private LiveMapViewModel? _viewModel;
    private SceneColors? _colors;
    private bool _isRenderingHooked;
    private double _lastFrameSeconds;
    private int _framesThisSecond;
    private double _frameCountStart;
    private int _framesPerSecond;
    private Point3D _lastWrittenPosition;
    private Vector3D _lastWrittenLook;
    private Point? _mouseDownAt;
    private Point _lastMousePosition;
    private MouseButton? _dragButton;
    private bool _isDragging;

    public LiveMapView() {
        InitializeComponent();
        Viewport.EffectsManager = new DefaultEffectsManager();
        Viewport.ViewCubeTexture = ViewCubeTexture.Create(this);

        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += (_, _) => UpdateRenderingHook();
        Loaded += (_, _) => UpdateRenderingHook();
        Unloaded += (_, _) => UnhookRendering();

        Viewport.PreviewMouseDown += OnViewportMouseDown;
        Viewport.PreviewMouseMove += OnViewportMouseMove;
        Viewport.PreviewMouseUp += OnViewportMouseUp;
        Viewport.PreviewMouseWheel += OnViewportMouseWheel;
        Viewport.PreviewKeyDown += OnViewportKeyDown;
        Viewport.PreviewKeyUp += (_, e) => _heldKeys.Remove(e.Key);
        Viewport.LostKeyboardFocus += (_, _) => _heldKeys.Clear();
    }

    /// <summary>
    /// The row menu acts on the selected player, so opening it (right click
    /// or the menu key) selects that row first; the card shows who it is.
    /// </summary>
    private void OnPlayerRowContextMenuOpening(object sender, ContextMenuEventArgs e) {
        if (_viewModel != null && sender is FrameworkElement { DataContext: PlayerRowViewModel row }) {
            _viewModel.SelectPlayerCommand.Execute(row);
        }
    }

    /// <summary>Gives the keyboard to the 3D view, so the camera keys work at once.</summary>
    public void FocusViewport() {
        Viewport.Focus();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) {
        if (_viewModel != null) {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
        _viewModel = DataContext as LiveMapViewModel;
        if (_viewModel != null) {
            _viewModel.PropertyChanged += OnViewModelChanged;
            ShowWireframe(_viewModel.Wireframe);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(LiveMapViewModel.Wireframe) && _viewModel != null) {
            ShowWireframe(_viewModel.Wireframe);
        }
        if (e.PropertyName == nameof(LiveMapViewModel.NavMesh) && _viewModel != null) {
            ShowNavMesh(_viewModel.NavMesh);
        }
    }

    private SceneColors Colors => _colors ??= SceneColors.FromResources(this);

    private void ShowWireframe(BspWireframe? wireframe) {
        if (wireframe == null) {
            MapLines.Geometry = null;
            GridLines.Geometry = null;
            return;
        }

        MapLines.Geometry = WireframeScene.BuildMapLines(wireframe, Colors.WireLow, Colors.WireHigh);
        // Lines far from the camera fade to 35 %, so the near part of the map reads first.
        MapLines.Material = new LineMaterial {
            Color = System.Windows.Media.Colors.White,
            Thickness = 1,
            Smoothness = 0.5,
            EnableDistanceFading = true,
            FadingNearDistance = 0,
            FadingFarDistance = _viewModel?.Camera.FitDistance * 2.2 ?? 10000,
        };

        GridLines.Geometry = WireframeScene.BuildFloorGrid(wireframe);
        GridLines.Color = ToMediaColor(WireframeScene.Blend(Colors.WireGrid, Colors.Background, 0.8f));
        WriteCamera(force: true);
    }

    private void ShowNavMesh(NavMesh? mesh) {
        if (mesh == null || mesh.Areas.Count == 0) {
            NavAreaLines.Geometry = null;
            NavLinkLines.Geometry = null;
            NavHidingSpots.Geometry = null;
            return;
        }

        (LineGeometry3D areas, LineGeometry3D? links, PointGeometry3D? spots) = WireframeScene.BuildNavMesh(mesh);
        NavAreaLines.Geometry = areas;
        NavLinkLines.Geometry = links;
        NavHidingSpots.Geometry = spots;
    }

    private void UpdateRenderingHook() {
        if (IsVisible && IsLoaded) {
            if (!_isRenderingHooked) {
                CompositionTarget.Rendering += OnRendering;
                _isRenderingHooked = true;
                _lastFrameSeconds = _frameClock.Elapsed.TotalSeconds;
            }
            return;
        }
        UnhookRendering();
    }

    private void UnhookRendering() {
        if (_isRenderingHooked) {
            CompositionTarget.Rendering -= OnRendering;
            _isRenderingHooked = false;
        }
        _heldKeys.Clear();
    }

    private void OnRendering(object? sender, EventArgs e) {
        if (_viewModel == null) {
            return;
        }

        double nowSeconds = _frameClock.Elapsed.TotalSeconds;
        float deltaSeconds = (float)Math.Clamp(nowSeconds - _lastFrameSeconds, 0, 0.1);
        _lastFrameSeconds = nowSeconds;
        CountFrame(nowSeconds);

        DateTime now = DateTime.UtcNow;
        IReadOnlyList<PlayerRenderState> players = _viewModel.GetRenderStates(now);

        ApplyHeldKeys(deltaSeconds);
        FollowSelectedPlayer(players, deltaSeconds);

        if (_viewModel.Camera.Update(deltaSeconds)) {
            WriteCamera(force: false);
        } else {
            AdoptExternalCameraMove();
        }

        UpdatePlayers(players);

        if (_overlayClock.ElapsedMilliseconds >= 200) {
            _overlayClock.Restart();
            UpdateCameraOverlay();
        }
    }

    private void CountFrame(double nowSeconds) {
        _framesThisSecond++;
        if (nowSeconds - _frameCountStart >= 1) {
            _framesPerSecond = _framesThisSecond;
            _framesThisSecond = 0;
            _frameCountStart = nowSeconds;
        }
    }

    private void FollowSelectedPlayer(IReadOnlyList<PlayerRenderState> players, float deltaSeconds) {
        if (_viewModel == null || !_viewModel.IsFollowing) {
            return;
        }
        PlayerRenderState? selected = players.FirstOrDefault(player => player.UserId == _viewModel.SelectedUserId);
        if (selected == null) {
            return;
        }
        var head = new System.Numerics.Vector3(selected.Origin.X, selected.Origin.Y, selected.Origin.Z + LiveMapViewModel.HeadOffset);
        _viewModel.Camera.FollowTowards(head, deltaSeconds);
    }

    private void WriteCamera(bool force) {
        if (_viewModel == null) {
            return;
        }

        CameraPose pose = _viewModel.Camera.GetPose();
        var position = new Point3D(pose.Position.X, pose.Position.Y, pose.Position.Z);
        var look = new Vector3D(pose.LookDirection.X, pose.LookDirection.Y, pose.LookDirection.Z);
        if (!force && position == _lastWrittenPosition && look == _lastWrittenLook) {
            return;
        }

        Camera.Position = position;
        Camera.LookDirection = look;
        Camera.UpDirection = new Vector3D(pose.UpDirection.X, pose.UpDirection.Y, pose.UpDirection.Z);
        Camera.FarPlaneDistance = Math.Max(20000, _viewModel.Camera.Distance + _viewModel.Camera.MapRadius * 3);
        _lastWrittenPosition = position;
        _lastWrittenLook = look;
    }

    /// <summary>
    /// The ViewCube moves the Helix camera on its own. When that happened,
    /// the rig takes the new pose over, so toolbar and mouse continue from it.
    /// </summary>
    private void AdoptExternalCameraMove() {
        if (_viewModel == null) {
            return;
        }

        bool movedByHelix = (Camera.Position - _lastWrittenPosition).Length > 0.5
            || (Camera.LookDirection - _lastWrittenLook).Length > 0.5;
        if (!movedByHelix) {
            return;
        }

        var position = new System.Numerics.Vector3((float)Camera.Position.X, (float)Camera.Position.Y, (float)Camera.Position.Z);
        var look = new System.Numerics.Vector3((float)Camera.LookDirection.X, (float)Camera.LookDirection.Y, (float)Camera.LookDirection.Z);
        _lastWrittenPosition = Camera.Position;
        _lastWrittenLook = Camera.LookDirection;
        _viewModel.OnManualCameraAction();
        _viewModel.Camera.Adopt(position, look);
        _viewModel.IsTopView = _viewModel.Camera.IsTopView;
    }

    private void UpdateCameraOverlay() {
        Point3D position = Camera.Position;
        Vector3D look = Camera.LookDirection;
        if (look.Length > 0) {
            look.Normalize();
        }
        Vector3D up = Camera.UpDirection;

        CameraPositionText.Text = string.Create(CultureInfo.InvariantCulture, $"Position {position.X:0}, {position.Y:0}, {position.Z:0}");
        CameraLookText.Text = string.Create(CultureInfo.InvariantCulture, $"Look {look.X:0.00}, {look.Y:0.00}, {look.Z:0.00}");
        CameraUpText.Text = string.Create(CultureInfo.InvariantCulture, $"Up {up.X:0}, {up.Y:0}, {up.Z:0}");
        FrameRateText.Text = $"{_framesPerSecond} FPS";
    }

    // Players -----------------------------------------------------------------

    private void UpdatePlayers(IReadOnlyList<PlayerRenderState> players) {
        if (_viewModel == null) {
            return;
        }

        Matrix viewProjection = BuildViewProjection();
        float? ringRadius = SelectionRingRadius(players);
        var look = new Vector3((float)Camera.LookDirection.X, (float)Camera.LookDirection.Y, (float)Camera.LookDirection.Z);

        IReadOnlyList<VisibleShot> shots = _viewModel.GetVisibleShots(DateTime.UtcNow);
        PlayerLines lines = PlayerSceneBuilder.Build(players, _viewModel.SelectedUserId, ringRadius, _viewModel.ShowTrails, shots, look, Colors);
        PlayerThinLines.Geometry = NullIfEmpty(lines.Thin);
        PlayerMediumLines.Geometry = NullIfEmpty(lines.Medium);
        PlayerWideLines.Geometry = NullIfEmpty(lines.Wide);
        PlayerHeads.Geometry = BuildHeadPoints(players);
        PlayerGlows.Geometry = BuildGlowPoints(players, look);

        UpdateNameLabels(players, viewProjection);
    }

    private static LineGeometry3D? NullIfEmpty(LineGeometry3D geometry) {
        // Helix does not like geometry without vertices.
        return geometry.Positions == null || geometry.Positions.Count == 0 ? null : geometry;
    }

    /// <summary>
    /// Head markers as screen sized points: a 10 unit sphere is two pixels
    /// at map scale, a fixed size dot stays readable at every zoom level.
    /// </summary>
    private PointGeometry3D? BuildHeadPoints(IReadOnlyList<PlayerRenderState> players) {
        var positions = new Vector3Collection();
        var colors = new Color4Collection();
        foreach (PlayerRenderState player in players.Where(candidate => candidate.IsAlive)) {
            positions.Add(PlayerSceneBuilder.ToVector(player.Origin) + new Vector3(0, 0, LiveMapViewModel.HeadOffset));
            colors.Add(WireframeScene.ToColor4(Colors.TeamColor(player.Team), 1f));
        }
        if (positions.Count == 0) {
            return null;
        }
        return new PointGeometry3D { Positions = positions, Colors = colors };
    }

    /// <summary>
    /// A soft disc in the glow color behind the head of every glowing player,
    /// like the glow shell in game. Mixed into the background instead of
    /// alpha (see WireframeScene.Blend), and pushed a few units away from the
    /// camera so the head marker always wins the depth test.
    /// </summary>
    private PointGeometry3D? BuildGlowPoints(IReadOnlyList<PlayerRenderState> players, Vector3 look) {
        const float BehindHead = 8f;
        const float GlowOpacity = 0.55f;

        Vector3 away = look.LengthSquared() > 0 ? Vector3.Normalize(look) * BehindHead : Vector3.Zero;
        var positions = new Vector3Collection();
        var colors = new Color4Collection();
        foreach (PlayerRenderState player in players) {
            if (!player.IsAlive || player.Glow == null) {
                continue;
            }
            PlayerGlow glow = player.Glow.Value;
            positions.Add(PlayerSceneBuilder.ToVector(player.Origin) + new Vector3(0, 0, LiveMapViewModel.HeadOffset) + away);
            colors.Add(WireframeScene.Blend(MediaColor.FromRgb(glow.Red, glow.Green, glow.Blue), Colors.Background, GlowOpacity));
        }
        if (positions.Count == 0) {
            return null;
        }
        return new PointGeometry3D { Positions = positions, Colors = colors };
    }

    /// <summary>The ring around the selected marker, sized to stay about 9 px on screen.</summary>
    private float? SelectionRingRadius(IReadOnlyList<PlayerRenderState> players) {
        if (_viewModel?.SelectedUserId == null || Viewport.ActualHeight <= 0) {
            return null;
        }
        PlayerRenderState? selected = players.FirstOrDefault(player => player.UserId == _viewModel.SelectedUserId);
        if (selected == null) {
            return null;
        }

        Vector3 head = PlayerSceneBuilder.ToVector(selected.Origin) + new Vector3(0, 0, LiveMapViewModel.HeadOffset);
        var cameraPosition = new Vector3((float)Camera.Position.X, (float)Camera.Position.Y, (float)Camera.Position.Z);
        float distance = Vector3.Distance(head, cameraPosition);
        float unitsPerPixel = 2 * distance * MathF.Tan((float)(Camera.FieldOfView * Math.PI / 360)) / (float)Viewport.ActualHeight;
        return unitsPerPixel * 9.5f;
    }

    /// <summary>
    /// Names are WPF text on top of the viewport, placed at the projected
    /// head position. Helix billboard text clipped longer names, and plain
    /// WPF text uses the app's font and stays sharp.
    /// </summary>
    private void UpdateNameLabels(IReadOnlyList<PlayerRenderState> players, Matrix viewProjection) {
        var seen = new HashSet<int>();
        bool showNames = _viewModel != null && _viewModel.ShowNames;
        bool showWeapons = _viewModel != null && _viewModel.ShowWeapons;

        foreach (PlayerRenderState player in players) {
            seen.Add(player.UserId);
            if (!_labels.TryGetValue(player.UserId, out NameLabel? label)) {
                label = CreateLabel();
                _labels[player.UserId] = label;
            }
            if (label.Text.Text != player.Name) {
                label.Text.Text = player.Name;
            }
            label.Text.Visibility = showNames ? Visibility.Visible : Visibility.Collapsed;
            if (label.Weapon != player.Weapon) {
                label.Weapon = player.Weapon;
                label.Icon.Source = _viewModel?.WeaponIconFor(player.Weapon);
            }
            bool hasIcon = showWeapons && label.Icon.Source != null;
            label.Icon.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;
            // A gap between icon and name only when both are there.
            label.Icon.Margin = showNames ? new Thickness(0, 0, 4, 0) : new Thickness(0);

            Vector3 head = PlayerSceneBuilder.ToVector(player.Origin) + new Vector3(0, 0, LiveMapViewModel.HeadOffset);
            Point? screen = Project(head, viewProjection);
            bool visible = (showNames || hasIcon) && player.IsAlive && screen != null;
            label.Box.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) {
                // Up and to the right of the marker, like a callout.
                Canvas.SetLeft(label.Box, Math.Round(screen!.Value.X + 8));
                Canvas.SetTop(label.Box, Math.Round(screen.Value.Y - 22));
            }
        }

        foreach (int gone in _labels.Keys.Where(userId => !seen.Contains(userId)).ToList()) {
            LabelLayer.Children.Remove(_labels[gone].Box);
            _labels.Remove(gone);
        }
    }

    private NameLabel CreateLabel() {
        var text = new TextBlock {
            FontSize = (double)FindResource("FontSizeCaption"),
            Foreground = (Brush)FindResource("TextPrimary"),
        };
        var icon = new Image {
            Height = (double)FindResource("WeaponIconHeight"),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.NearestNeighbor);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(text);
        var box = new Border {
            Background = (Brush)FindResource("ViewportLabelBackground"),
            Padding = new Thickness(4, 1, 4, 1),
            Child = content,
            IsHitTestVisible = false,
        };
        LabelLayer.Children.Add(box);
        return new NameLabel(box, text, icon);
    }

    /// <summary>Screen position of a world point, null when it is behind the camera.</summary>
    private Point? Project(Vector3 world, Matrix viewProjection) {
        Vector4 clip = Vector3.Transform(world, viewProjection);
        if (clip.W <= 0) {
            return null;
        }
        double x = (clip.X / clip.W + 1) / 2 * Viewport.ActualWidth;
        double y = (1 - clip.Y / clip.W) / 2 * Viewport.ActualHeight;
        return new Point(x, y);
    }

    private static MediaColor ToMediaColor(Color4 color) {
        return MediaColor.FromArgb((byte)(color.Alpha * 255), (byte)(color.Red * 255), (byte)(color.Green * 255), (byte)(color.Blue * 255));
    }

    // Input -------------------------------------------------------------------

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e) {
        Viewport.Focus();
        if (IsOverHelixOverlay(e.GetPosition(Viewport))) {
            // ViewCube and axes handle their own clicks.
            return;
        }

        _mouseDownAt = e.GetPosition(Viewport);
        _lastMousePosition = _mouseDownAt.Value;
        _dragButton = e.ChangedButton;
        _isDragging = false;
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e) {
        if (_mouseDownAt == null || _viewModel == null || _dragButton == null) {
            return;
        }

        Point position = e.GetPosition(Viewport);
        if (!_isDragging && (position - _mouseDownAt.Value).Length < DragThreshold) {
            return;
        }
        _isDragging = true;

        Vector delta = position - _lastMousePosition;
        _lastMousePosition = position;
        _viewModel.OnManualCameraAction();

        if (_dragButton == MouseButton.Right) {
            _viewModel.Camera.OrbitByPixels(delta.X, delta.Y);
            _viewModel.IsTopView = _viewModel.Camera.IsTopView;
        } else {
            _viewModel.Camera.MoveByPixels(delta.X, delta.Y, Viewport.ActualHeight);
        }
        e.Handled = true;
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e) {
        if (_mouseDownAt == null) {
            return;
        }

        bool wasClick = !_isDragging && e.ChangedButton == MouseButton.Left;
        Point position = e.GetPosition(Viewport);
        _mouseDownAt = null;
        _dragButton = null;
        _isDragging = false;
        Viewport.ReleaseMouseCapture();
        e.Handled = true;

        if (wasClick) {
            PickPlayerAt(position);
        }
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e) {
        if (_viewModel == null) {
            return;
        }
        _viewModel.OnManualCameraAction();
        // One notch is about 10 %, the same feel near and far.
        _viewModel.Camera.ZoomScale(e.Delta > 0 ? 0.9f : 1f / 0.9f);
        e.Handled = true;
    }

    private void OnViewportKeyDown(object sender, KeyEventArgs e) {
        if (_viewModel == null) {
            return;
        }

        switch (e.Key) {
            case Key.Home:
                _viewModel.ResetViewCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.T:
                _viewModel.IsTopView = !_viewModel.IsTopView;
                e.Handled = true;
                return;
        }

        if (IsCameraKey(e.Key)) {
            _heldKeys.Add(e.Key);
            e.Handled = true;
        }
    }

    private static bool IsCameraKey(Key key) {
        return key is Key.W or Key.A or Key.S or Key.D or Key.Up or Key.Down or Key.Left or Key.Right
            or Key.Q or Key.E or Key.R or Key.F or Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract;
    }

    /// <summary>Held keys move the camera continuously, scaled by the frame time.</summary>
    private void ApplyHeldKeys(float deltaSeconds) {
        if (_viewModel == null || _heldKeys.Count == 0) {
            return;
        }

        CameraRig camera = _viewModel.Camera;
        float moveSpeed = camera.Distance * 0.6f * deltaSeconds;
        float right = 0;
        float forward = 0;
        if (_heldKeys.Contains(Key.W) || _heldKeys.Contains(Key.Up)) {
            forward += moveSpeed;
        }
        if (_heldKeys.Contains(Key.S) || _heldKeys.Contains(Key.Down)) {
            forward -= moveSpeed;
        }
        if (_heldKeys.Contains(Key.D) || _heldKeys.Contains(Key.Right)) {
            right += moveSpeed;
        }
        if (_heldKeys.Contains(Key.A) || _heldKeys.Contains(Key.Left)) {
            right -= moveSpeed;
        }
        if (right != 0 || forward != 0) {
            camera.Move(right, forward, animate: false);
        }

        if (_heldKeys.Contains(Key.Q)) {
            camera.RotateBy(-KeyboardOrbitDegreesPerSecond * deltaSeconds, animate: false);
        }
        if (_heldKeys.Contains(Key.E)) {
            camera.RotateBy(KeyboardOrbitDegreesPerSecond * deltaSeconds, animate: false);
        }
        if (_heldKeys.Contains(Key.R)) {
            camera.PitchBy(KeyboardTiltDegreesPerSecond * deltaSeconds, animate: false);
        }
        if (_heldKeys.Contains(Key.F)) {
            camera.PitchBy(-KeyboardTiltDegreesPerSecond * deltaSeconds, animate: false);
        }
        if (_heldKeys.Contains(Key.OemPlus) || _heldKeys.Contains(Key.Add)) {
            camera.ZoomScale(1f - KeyboardZoomPerSecond * deltaSeconds * 0.5f);
        }
        if (_heldKeys.Contains(Key.OemMinus) || _heldKeys.Contains(Key.Subtract)) {
            camera.ZoomScale(1f + KeyboardZoomPerSecond * deltaSeconds * 0.5f);
        }

        _viewModel.OnManualCameraAction();
        _viewModel.IsTopView = camera.IsTopView;
    }

    private bool IsOverHelixOverlay(Point position) {
        // The ViewCube sits bottom right, the axes bottom left; both about 100 px.
        double width = Viewport.ActualWidth;
        double height = Viewport.ActualHeight;
        bool nearBottom = position.Y > height - 130;
        bool nearRight = position.X > width - 150;
        return nearBottom && nearRight;
    }

    /// <summary>Selects the alive player whose head marker is closest to the click on screen.</summary>
    private void PickPlayerAt(Point click) {
        if (_viewModel == null || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) {
            return;
        }

        Matrix viewProjection = BuildViewProjection();
        int? bestUserId = null;
        double bestDistance = PickRadiusPixels;

        foreach (PlayerRenderState player in _viewModel.GetRenderStates(DateTime.UtcNow)) {
            if (!player.IsAlive) {
                continue;
            }
            Vector3 head = PlayerSceneBuilder.ToVector(player.Origin) + new Vector3(0, 0, LiveMapViewModel.HeadOffset);
            Vector4 clip = Vector3.Transform(head, viewProjection);
            if (clip.W <= 0) {
                continue;
            }

            double screenX = (clip.X / clip.W + 1) / 2 * Viewport.ActualWidth;
            double screenY = (1 - clip.Y / clip.W) / 2 * Viewport.ActualHeight;
            double distance = (new Point(screenX, screenY) - click).Length;
            if (distance < bestDistance) {
                bestDistance = distance;
                bestUserId = player.UserId;
            }
        }

        if (bestUserId != null) {
            _viewModel.SelectByUserId(bestUserId.Value);
        }
    }

    private Matrix BuildViewProjection() {
        var position = new Vector3((float)Camera.Position.X, (float)Camera.Position.Y, (float)Camera.Position.Z);
        var look = new Vector3((float)Camera.LookDirection.X, (float)Camera.LookDirection.Y, (float)Camera.LookDirection.Z);
        var up = new Vector3((float)Camera.UpDirection.X, (float)Camera.UpDirection.Y, (float)Camera.UpDirection.Z);
        Matrix view = Matrix.LookAtRH(position, position + look, up);
        float aspect = (float)(Viewport.ActualWidth / Viewport.ActualHeight);
        Matrix projection = Matrix.PerspectiveFovRH((float)(Camera.FieldOfView * Math.PI / 180), aspect,
            (float)Camera.NearPlaneDistance, (float)Camera.FarPlaneDistance);
        return view * projection;
    }

    /// <summary>Weapon remembers what the icon shows, so the bitmap is only swapped on a weapon change.</summary>
    private sealed record NameLabel(Border Box, TextBlock Text, Image Icon) {
        public string? Weapon { get; set; }
    }
}
