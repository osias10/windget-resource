using System;
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using ThemeMode = ResourceStatus.Core.ThemeMode;

namespace ResourceStatus.App;

public static class Theme
{
    private static readonly Palette PaletteSource = new();
    private static Application? _styledApplication;
    private static ThemeMode _mode = ThemeMode.Dark;

    public static SolidColorBrush Text { get; } = Brush(nameof(Palette.Text));
    public static SolidColorBrush Muted { get; } = Brush(nameof(Palette.Muted));
    public static SolidColorBrush Border { get; } = Brush(nameof(Palette.Border));
    public static SolidColorBrush Background { get; } = Brush(nameof(Palette.Background));
    public static SolidColorBrush Surface { get; } = Brush(nameof(Palette.Surface));
    public static SolidColorBrush Hover { get; } = Brush(nameof(Palette.Hover));
    public static SolidColorBrush Track { get; } = Brush(nameof(Palette.Track));
    public static SolidColorBrush Cpu { get; } = Brush(nameof(Palette.Cpu));
    public static SolidColorBrush Ram { get; } = Brush(nameof(Palette.Ram));
    public static SolidColorBrush Gpu { get; } = Brush(nameof(Palette.Gpu));
    public static SolidColorBrush Download { get; } = Brush(nameof(Palette.Download));
    public static SolidColorBrush Upload { get; } = Brush(nameof(Palette.Upload));
    public static SolidColorBrush Warning { get; } = Brush(nameof(Palette.Warning));
    public static SolidColorBrush UsageWarning { get; } = Brush(nameof(Palette.UsageWarning));
    public static SolidColorBrush UsageCritical { get; } = Brush(nameof(Palette.UsageCritical));
    public static FontFamily FontFamily { get; } = new("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
    public static bool IsDark { get; private set; } = true;
    public static event EventHandler? Changed;

    private static SolidColorBrush Brush(string property)
    {
        // Keep the same brush instances for existing controls and retained chart drawings.
        // A binding also prevents Style.Seal from freezing these shared Freezables.
        var brush = new SolidColorBrush();
        BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty,
            new Binding(property) { Source = PaletteSource, Mode = BindingMode.OneWay });
        return brush;
    }

    public static void Apply(ThemeMode mode = ThemeMode.Dark)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Apply(mode));
            return;
        }

        _mode = Enum.IsDefined(mode) ? mode : ThemeMode.Dark;
        SetPalette(_mode == ThemeMode.System ? ReadSystemDarkMode() : _mode == ThemeMode.Dark);
        if (Application.Current is not null && !ReferenceEquals(_styledApplication, Application.Current))
        {
            InitializeResources();
            _styledApplication = Application.Current;
        }
    }

    /// <summary>Re-read the Windows app preference without changing explicit light/dark choices.</summary>
    public static void RefreshSystemTheme()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RefreshSystemTheme);
            return;
        }
        if (_mode == ThemeMode.System) SetPalette(ReadSystemDarkMode());
    }

    private static void SetPalette(bool dark)
    {
        if (IsDark == dark) return;
        IsDark = dark;
        PaletteSource.SetDark(dark);
        // Native backdrop tint is computed inside Changed handlers, so publish fully
        // updated brush colors even if a binding transfer was queued by WPF.
        foreach (var brush in new[] { Text, Muted, Border, Background, Surface, Hover, Track, Cpu, Ram, Gpu, Download, Upload, Warning, UsageWarning, UsageCritical })
            BindingOperations.GetBindingExpression(brush, SolidColorBrush.ColorProperty)?.UpdateTarget();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool ReadSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // An unset Windows app preference defaults to light. A denied read retains
            // the current effective palette instead of unexpectedly changing the UI.
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            return IsDark;
        }
    }

    private sealed class Palette : INotifyPropertyChanged
    {
        private bool _dark = true;
        public event PropertyChangedEventHandler? PropertyChanged;
        public Color Text => Color("#F0F0F1", "#17191F");
        public Color Muted => Color("#ABACB4", "#5C616D");
        public Color Border => Color("#36373C", "#C7CBD3");
        public Color Background => Color("#191A1D", "#F7F8FA");
        public Color Surface => Color("#222328", "#FFFFFF");
        public Color Hover => Color("#303137", "#E5E9F0");
        public Color Track => Color("#34353A", "#D7DCE5");
        public Color Cpu => Color("#8FADE3", "#315FA6");
        public Color Ram => Color("#B59BE8", "#7950B5");
        public Color Gpu => Color("#6DD5C5", "#117A69");
        public Color Download => Color("#7FB7ED", "#2668A7");
        public Color Upload => Color("#E1B879", "#8F5B10");
        public Color Warning => Color("#D6AC70", "#8B590E");
        public Color UsageWarning => Color("#FFAA4C", "#A34B00");
        public Color UsageCritical => Color("#FF7078", "#C32432");

        private Color Color(string dark, string light) => (Color)ColorConverter.ConvertFromString(_dark ? dark : light);

        public void SetDark(bool dark)
        {
            _dark = dark;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    private static void InitializeResources()
    {
        var resources = Application.Current.Resources;
        resources["TextBrush"] = Text;
        resources["MutedBrush"] = Muted;
        resources["BorderBrush"] = Border;
        resources["WidgetBrush"] = Background;
        resources["SurfaceBrush"] = Surface;
        resources["TrackBrush"] = Track;
        resources["HoverBrush"] = Hover;
        resources["AccentBrush"] = Cpu;

        var window = new Style(typeof(Window));
        window.Setters.Add(new Setter(Control.FontFamilyProperty, FontFamily));
        window.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        window.Setters.Add(new Setter(Control.BackgroundProperty, Background));
        resources[typeof(Window)] = window;

        var text = new Style(typeof(TextBlock));
        text.Setters.Add(new Setter(TextBlock.ForegroundProperty, Text));
        text.Setters.Add(new Setter(TextBlock.FontFamilyProperty, FontFamily));
        resources[typeof(TextBlock)] = text;

        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Border));
        button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        button.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        button.Setters.Add(new Setter(Control.FontFamilyProperty, FontFamily));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.Name = "ButtonBorder";
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, Hover, "ButtonBorder"));
        template.Triggers.Add(hover);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, Hover, "ButtonBorder"));
        template.Triggers.Add(focus);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45));
        template.Triggers.Add(disabled);
        button.Setters.Add(new Setter(Control.TemplateProperty, template));
        resources[typeof(Button)] = button;

        foreach (var type in new[] { typeof(CheckBox), typeof(RadioButton), typeof(TabControl), typeof(TabItem), typeof(ComboBox), typeof(ContextMenu), typeof(MenuItem), typeof(ToolTip) })
        {
            var style = new Style(type);
            style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Border));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, FontFamily));
            resources[type] = style;
        }
        // Popups must follow the app's effective palette, including explicit OS overrides.
        resources[SystemColors.WindowBrushKey] = Surface;
        resources[SystemColors.WindowTextBrushKey] = Text;
        resources[SystemColors.ControlBrushKey] = Surface;
        resources[SystemColors.ControlTextBrushKey] = Text;
        resources[SystemColors.HighlightBrushKey] = Hover;
        resources[SystemColors.HighlightTextBrushKey] = Text;
        resources[SystemColors.MenuBrushKey] = Surface;
        resources[SystemColors.MenuTextBrushKey] = Text;

        var controls = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style x:Key="SliderTrackButton" TargetType="RepeatButton">
                <Setter Property="Focusable" Value="False" />
                <Setter Property="IsTabStop" Value="False" />
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="RepeatButton">
                      <Grid Background="Transparent">
                        <Border Height="4" VerticalAlignment="Center" CornerRadius="2"
                          Background="{TemplateBinding Background}" />
                      </Grid>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style x:Key="SliderThumb" TargetType="Thumb">
                <Setter Property="Width" Value="14" />
                <Setter Property="Height" Value="14" />
                <Setter Property="VerticalAlignment" Value="Center" />
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="Thumb">
                      <Grid Background="Transparent">
                        <Border x:Name="ThumbRing" Margin="-3" CornerRadius="10" BorderThickness="1"
                          BorderBrush="{DynamicResource AccentBrush}" Opacity="0" />
                        <Ellipse Fill="{DynamicResource AccentBrush}" Stroke="{DynamicResource SurfaceBrush}" StrokeThickness="1" />
                      </Grid>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                          <Setter TargetName="ThumbRing" Property="Opacity" Value="0.7" />
                        </Trigger>
                        <Trigger Property="IsDragging" Value="True">
                          <Setter TargetName="ThumbRing" Property="Opacity" Value="1" />
                        </Trigger>
                        <DataTrigger Binding="{Binding IsKeyboardFocusWithin, RelativeSource={RelativeSource AncestorType=Slider}}" Value="True">
                          <Setter TargetName="ThumbRing" Property="Opacity" Value="1" />
                          <Setter TargetName="ThumbRing" Property="BorderThickness" Value="2" />
                        </DataTrigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="Slider">
                <Setter Property="MinHeight" Value="24" />
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}" />
                <Setter Property="FocusVisualStyle" Value="{x:Null}" />
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="Slider">
                      <Grid x:Name="SliderRoot" Background="Transparent" Margin="3,0" SnapsToDevicePixels="True">
                        <Track x:Name="PART_Track" Orientation="Horizontal"
                          Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                          IsDirectionReversed="{TemplateBinding IsDirectionReversed}"
                          Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                          <Track.DecreaseRepeatButton>
                            <RepeatButton Style="{StaticResource SliderTrackButton}"
                              Command="{x:Static Slider.DecreaseLarge}" Background="{DynamicResource AccentBrush}" />
                          </Track.DecreaseRepeatButton>
                          <Track.Thumb>
                            <Thumb Style="{StaticResource SliderThumb}" />
                          </Track.Thumb>
                          <Track.IncreaseRepeatButton>
                            <RepeatButton Style="{StaticResource SliderTrackButton}"
                              Command="{x:Static Slider.IncreaseLarge}" Background="{DynamicResource TrackBrush}" />
                          </Track.IncreaseRepeatButton>
                        </Track>
                      </Grid>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsEnabled" Value="False">
                          <Setter TargetName="SliderRoot" Property="Opacity" Value="0.4" />
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="CheckBox">
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}" />
                <Setter Property="VerticalContentAlignment" Value="Center" />
                <Setter Property="FocusVisualStyle" Value="{x:Null}" />
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="CheckBox">
                      <Grid x:Name="CheckRoot" Background="Transparent">
                        <Grid.ColumnDefinitions>
                          <ColumnDefinition Width="Auto" />
                          <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <Grid Width="14" Height="14" VerticalAlignment="Center">
                          <Border x:Name="FocusRing" Margin="-3" CornerRadius="4" BorderThickness="1"
                            BorderBrush="{DynamicResource AccentBrush}" Visibility="Collapsed" />
                          <Border x:Name="CheckSurface" Background="{DynamicResource SurfaceBrush}"
                            BorderBrush="{DynamicResource MutedBrush}" BorderThickness="1" CornerRadius="2" />
                          <Path x:Name="CheckMark" Data="M 3,7 L 6,10 L 11,4" Stroke="{DynamicResource WidgetBrush}"
                            StrokeThickness="1.8" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" Visibility="Collapsed" />
                          <Path x:Name="IndeterminateMark" Data="M 3,7 L 11,7" Stroke="{DynamicResource WidgetBrush}"
                            StrokeThickness="1.8" Visibility="Collapsed" />
                        </Grid>
                        <ContentPresenter Grid.Column="1" Margin="7,0,0,0" RecognizesAccessKey="True"
                          HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
                      </Grid>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                          <Setter TargetName="CheckSurface" Property="BorderBrush" Value="{DynamicResource AccentBrush}" />
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                          <Setter TargetName="CheckSurface" Property="Background" Value="{DynamicResource AccentBrush}" />
                          <Setter TargetName="CheckSurface" Property="BorderBrush" Value="{DynamicResource AccentBrush}" />
                          <Setter TargetName="CheckMark" Property="Visibility" Value="Visible" />
                        </Trigger>
                        <Trigger Property="IsChecked" Value="{x:Null}">
                          <Setter TargetName="CheckSurface" Property="Background" Value="{DynamicResource AccentBrush}" />
                          <Setter TargetName="CheckSurface" Property="BorderBrush" Value="{DynamicResource AccentBrush}" />
                          <Setter TargetName="IndeterminateMark" Property="Visibility" Value="Visible" />
                        </Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                          <Setter TargetName="FocusRing" Property="Visibility" Value="Visible" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                          <Setter TargetName="CheckRoot" Property="Opacity" Value="0.45" />
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="TabItem">
                <Setter Property="Foreground" Value="{DynamicResource MutedBrush}" />
                <Setter Property="Background" Value="Transparent" />
                <Setter Property="Padding" Value="14,8" />
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="TabItem">
                      <Border x:Name="Surface" Background="{TemplateBinding Background}" CornerRadius="4,4,0,0" Padding="{TemplateBinding Padding}">
                        <ContentPresenter ContentSource="Header" RecognizesAccessKey="True" HorizontalAlignment="Center" VerticalAlignment="Center" />
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsSelected" Value="True">
                          <Setter Property="Foreground" Value="{DynamicResource TextBrush}" />
                          <Setter TargetName="Surface" Property="Background" Value="{DynamicResource HoverBrush}" />
                        </Trigger>
                        <Trigger Property="IsMouseOver" Value="True">
                          <Setter TargetName="Surface" Property="Background" Value="{DynamicResource HoverBrush}" />
                        </Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                          <Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource AccentBrush}" />
                          <Setter TargetName="Surface" Property="BorderThickness" Value="1" />
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="ComboBox">
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}" />
                <Setter Property="Background" Value="{DynamicResource SurfaceBrush}" />
                <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}" />
                <Setter Property="BorderThickness" Value="1" />
                <Setter Property="Padding" Value="8,5" />
                <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled" />
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ComboBox">
                      <Grid>
                        <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press"
                            IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                          <ToggleButton.Template>
                            <ControlTemplate TargetType="ToggleButton">
                              <Border x:Name="ButtonSurface" Background="{DynamicResource SurfaceBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="4">
                                <TextBlock Text="⌄" Foreground="{DynamicResource MutedBrush}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,8,2" />
                              </Border>
                              <ControlTemplate.Triggers>
                                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="ButtonSurface" Property="Background" Value="{DynamicResource HoverBrush}" /></Trigger>
                              </ControlTemplate.Triggers>
                            </ControlTemplate>
                          </ToggleButton.Template>
                        </ToggleButton>
                        <ContentPresenter x:Name="SelectionPresenter" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                          ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"
                          ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}" Margin="8,5,28,5" VerticalAlignment="Center" IsHitTestVisible="False" />
                        <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                          <Border Background="{DynamicResource SurfaceBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="4"
                            MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="280">
                            <ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Margin="3">
                              <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained" />
                            </ScrollViewer>
                          </Border>
                        </Popup>
                      </Grid>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45" /></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="ComboBoxItem">
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}" />
                <Setter Property="Padding" Value="8,6" />
                <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ComboBoxItem">
                      <Border x:Name="ItemSurface" Background="Transparent" CornerRadius="3" Padding="{TemplateBinding Padding}">
                        <ContentPresenter />
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="ItemSurface" Property="Background" Value="{DynamicResource HoverBrush}" /></Trigger>
                        <Trigger Property="IsSelected" Value="True"><Setter TargetName="ItemSurface" Property="Background" Value="{DynamicResource HoverBrush}" /></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
            </ResourceDictionary>
            """);
        foreach (DictionaryEntry entry in controls) resources[entry.Key] = entry.Value;
    }
}
