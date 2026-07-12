# WPF → Avalonia Cheat Sheet

Practical reference for porting NTY ClassroomCtrl views from WPF (`net10.0-windows`)
to Avalonia 12.1 (`net10.0`). **Audience:** you know WPF; you're learning Avalonia.

Every example below is taken from the real Phase 24.3 `ConferenceTile` port, not
theory. Where a claim wasn't directly exercised in that port it's marked
_(not yet verified in this codebase)_.

> **Golden rules (read first)**
> 1. There are **no triggers** in Avalonia. `DataTrigger`/`Trigger` → **style
>    classes + selectors** (§3).
> 2. There is **no `Visibility`**. It's a `bool IsVisible` (§2, §4).
> 3. **A locally-set property outranks a style setter** (same as WPF). Any
>    property a class toggles must have its base value **in a style**, not inline (§6).
> 4. Turn on **`x:DataType` compiled bindings** — they catch binding typos at
>    build time (§7).

---

## 1. Imports / namespaces

| WPF | Avalonia |
|---|---|
| `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` | `xmlns="https://github.com/avaloniaui"` |
| `xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"` | **same** |
| `xmlns:local="clr-namespace:Foo"` | `xmlns:local="using:Foo"` |
| `xmlns:sys="clr-namespace:System;assembly=mscorlib"` | not needed — use `x:` intrinsics (`x:Double`, `x:Int32`, `x:Boolean`) |
| `.xaml` file extension | `.axaml` (convention; the SDK globs `**/*.axaml`) |
| `mc:Ignorable="d"` + `d:DesignWidth` | supported, optional (kept by the template) |

**Design-time namespaces** (`d:`, `mc:`) work in Avalonia but aren't required.

---

## 2. Property / element mappings

| WPF | Avalonia | Note |
|---|---|---|
| `Visibility="Visible/Collapsed"` | `IsVisible="True/False"` | **bool**, not a 3-state enum. No `Hidden` equivalent. |
| `Visibility="{Binding B, Converter={StaticResource BoolToVis}}"` | `IsVisible="{Binding B}"` | converter **deleted** |
| `<sys:Double x:Key="k">36</sys:Double>` | `<x:Double x:Key="k">36</x:Double>` | scalar resources |
| `System.Windows.Media.Imaging.BitmapSource` | `Avalonia.Media.Imaging.Bitmap` | image frames; `Image.Source` binds it directly |
| `MergedDictionaries` + `Source="Theme.xaml"` | `<ResourceInclude Source="avares://Asm/Path.axaml"/>` | `avares://` asset URI |
| `SnapsToDevicePixels="True"` | `UseLayoutRounding="True"` | _(not yet verified in this codebase)_ |
| `Panel.ZIndex="100"` | `ZIndex="100"` | attached → direct |
| `TextTrimming="CharacterEllipsis"` | **same** | |
| `<Run Text="{Binding X}"/>` inlines | **same** — `<Run>` inlines + bindings work | |
| `Foreground/Background` = `SolidColorBrush` | **same** | brushes port 1:1 incl. `Opacity` |
| `CornerRadius`, `Thickness`, `FontFamily` resources | **same** element+content syntax | |

### `x:Name`
Same attribute. In Avalonia, `x:Name`d elements are also referenceable in bindings
via the `#Name` selector: `{Binding #Root.SelfSuffix}` (element-name binding). No
`ElementName=` needed — use `#Name`.

### Merging a resource dictionary (real example)
```xml
<!-- App.axaml -->
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceInclude Source="avares://ClassroomCtrl.Avalonia.Sandbox/Themes/ConferenceDarkTheme.axaml"/>
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

---

## 3. Trigger translation — the big one

Avalonia has **no** `Style.Triggers`, `DataTrigger`, `Trigger`, or `EventTrigger`.
Replace them with **conditional style classes** (`Classes.foo="{Binding Bar}"`) and
**CSS-like selectors**.

### 3a. Simple boolean state
**WPF:**
```xml
<Style TargetType="Border">
  <Setter Property="BorderThickness" Value="0"/>
  <Style.Triggers>
    <DataTrigger Binding="{Binding IsSpeaking}" Value="True">
      <Setter Property="BorderBrush" Value="{StaticResource Ring}"/>
      <Setter Property="BorderThickness" Value="3"/>
    </DataTrigger>
  </Style.Triggers>
</Style>
```
**Avalonia:**
```xml
<!-- on the element: bind a class to the bool -->
<Border x:Name="OuterBorder" Classes.speaking="{Binding IsSpeaking}"/>

<!-- in <UserControl.Styles>: base + conditional selectors -->
<Style Selector="Border#OuterBorder">                 <!-- BASE (see Golden Rule 3) -->
  <Setter Property="BorderThickness" Value="0"/>
</Style>
<Style Selector="Border#OuterBorder.speaking">        <!-- when class present -->
  <Setter Property="BorderBrush" Value="{StaticResource Ring}"/>
  <Setter Property="BorderThickness" Value="3"/>
</Style>
```

### 3b. "Not null" / string state (no bool in the VM)
Use a converter to feed the class:
```xml
<Border Classes.live="{Binding JpegFrame, Converter={x:Static conv:ObjectConverters.IsNotNull}}"/>
<TextBlock IsVisible="{Binding Emoji, Converter={x:Static conv:StringConverters.IsNotNullOrEmpty}}"/>
```
(`xmlns:conv="using:Avalonia.Data.Converters"`)

### 3c. Shared style across many elements + per-element state
```xml
<!-- style once; toggle .on per element -->
<Style Selector="TextBlock.statusIcon">      <Setter Property="Foreground" Value="{StaticResource Red}"/></Style>
<Style Selector="TextBlock.statusIcon.on">   <Setter Property="Foreground" Value="{StaticResource White}"/></Style>

<TextBlock Text="🎙" Classes="statusIcon" Classes.on="{Binding IsMicLive}"/>
<TextBlock Text="📷" Classes="statusIcon" Classes.on="{Binding IsCamLive}"/>
```

### 3d. Multi-condition
Chain classes in the selector — `Selector="Border.a.b"` matches when **both**
classes are present (logical AND). For enum state, bind one class per enum value
(`Classes.pinned`, `Classes.speaking`) or use a converter that returns a class
name string _(not yet needed in this codebase)_.

### Selector quick reference
| Selector | Matches |
|---|---|
| `Border` | all Borders |
| `Border#OuterBorder` | the Border named `OuterBorder` |
| `Border.speaking` | Borders with class `speaking` |
| `Border#OuterBorder.speaking` | that named Border, when `speaking` is set |
| `TextBlock.a.b` | TextBlocks with **both** `a` and `b` |
| `Border > TextBlock` | direct child; `Border TextBlock` = any descendant |
| `Button:pointerover` | pseudo-class (WPF `IsMouseOver`) |

### 3e. Keyed `Style` (with `ControlTemplate`) → `ControlTheme`
_(added Phase 25.0-B, from the ConferenceDarkTheme button styles)_

A WPF **keyed** `<Style x:Key="X" TargetType="Button">` — applied via
`Style="{StaticResource X}"` and often carrying a `ControlTemplate` + template
triggers — has no `Style`-based equivalent in Avalonia. Use a **`ControlTheme`**,
applied via **`Theme="{StaticResource X}"`**:

```xml
<!-- WPF: <Style x:Key="Btn" TargetType="Button"> ... ControlTemplate.Triggers ... -->
<ControlTheme x:Key="Btn" TargetType="Button">
  <Setter Property="BorderBrush" Value="{StaticResource Hover}"/>
  <Setter Property="Cursor" Value="Hand"/>
  <Setter Property="Template">
    <ControlTemplate>
      <Border x:Name="PART_Bg"
              Background="{TemplateBinding Background}"          <!-- TemplateBinding: 1:1 -->
              BorderBrush="{TemplateBinding BorderBrush}">
        <ContentPresenter Content="{TemplateBinding Content}"
                          ContentTemplate="{TemplateBinding ContentTemplate}"
                          Foreground="{TemplateBinding Foreground}"/>
      </Border>
    </ControlTemplate>
  </Setter>
  <!-- WPF <Trigger Property="IsMouseOver"> on PART_Bg → pseudo-class selector.
       '^' = the templated control; '/template/' reaches into the template. -->
  <Style Selector="^:pointerover /template/ Border#PART_Bg">
    <Setter Property="Background" Value="{StaticResource HoverOverlay}"/>
  </Style>
</ControlTheme>

<!-- Inheritance: BasedOn works the same. -->
<ControlTheme x:Key="EndBtn" TargetType="Button" BasedOn="{StaticResource Btn}">
  <Setter Property="Background" Value="{StaticResource Red}"/>
</ControlTheme>

<!-- Apply: WPF Style="{StaticResource Btn}"  →  Avalonia Theme="{StaticResource Btn}" -->
<Button Theme="{StaticResource Btn}" Content="🎙"/>
```

| WPF | Avalonia |
|---|---|
| keyed `<Style TargetType>` | `<ControlTheme x:Key TargetType>` |
| applied via `Style="{StaticResource}"` | applied via `Theme="{StaticResource}"` |
| `<ControlTemplate.Triggers><Trigger IsMouseOver>` | nested `<Style Selector="^:pointerover /template/ …">` |
| `{TemplateBinding X}` | **same** |
| `BasedOn="{StaticResource}"` | **same** |
| `ContentPresenter` (auto content) | be explicit: bind `Content` + `ContentTemplate` |

> An *implicit* WPF style (`<Style TargetType="Button">` with no key) → an Avalonia
> `ControlTheme` set as the type's default, or a plain `<Style Selector="Button">`
> for non-template tweaks. _(implicit-default ControlTheme not yet exercised here)_

---

## 4. Converter translation

| WPF converter | Avalonia |
|---|---|
| `BooleanToVisibilityConverter` | **gone** — bind `IsVisible` to the bool directly |
| null→Visibility | `{x:Static conv:ObjectConverters.IsNotNull}` / `IsNull` |
| null→Visibility with `Inverted` param | just use the opposite: `IsNull` vs `IsNotNull` |
| empty-string check | `{x:Static conv:StringConverters.IsNotNullOrEmpty}` |
| **custom business logic** (e.g. `HexToBrush`) | **keep it** — implement `Avalonia.Data.Converters.IValueConverter` (same interface shape as WPF's `System.Windows.Data.IValueConverter`, minus the WPF namespaces) |

Built-ins live in `Avalonia.Data.Converters`: `ObjectConverters.IsNull/IsNotNull`,
`StringConverters.IsNotNullOrEmpty`, `BoolConverters.And/Or`. Reference via
`{x:Static conv:...}`.

---

## 5. DependencyProperty → StyledProperty

**WPF:**
```csharp
public static readonly DependencyProperty SelfSuffixProperty =
    DependencyProperty.Register(nameof(SelfSuffix), typeof(string),
        typeof(ConferenceTile), new PropertyMetadata(""));
public string SelfSuffix
{
    get => (string)GetValue(SelfSuffixProperty);
    set => SetValue(SelfSuffixProperty, value);
}
```
**Avalonia:**
```csharp
public static readonly StyledProperty<string> SelfSuffixProperty =
    AvaloniaProperty.Register<ConferenceTile, string>(nameof(SelfSuffix), "");
public string SelfSuffix
{
    get => GetValue(SelfSuffixProperty);   // generic — no cast
    set => SetValue(SelfSuffixProperty, value);
}
```

| Concern | WPF | Avalonia |
|---|---|---|
| Registration | `DependencyProperty.Register` | `AvaloniaProperty.Register<TOwner, T>` (generic) |
| Default value | `new PropertyMetadata(default)` | last arg of `Register<>()` |
| Get/Set | `(T)GetValue(...)` | `GetValue(...)` (typed, no cast) |
| Change callback | `PropertyMetadata(cb)` | override `OnPropertyChanged(AvaloniaPropertyChangedEventArgs)`, or `Property.Changed.AddClassHandler<T>(...)` _(not yet verified here)_ |
| Read-only DP | `RegisterReadOnly` | `DirectProperty` for VM-style props; `StyledProperty` for style-able ones _(not yet verified here)_ |
| Attached | `RegisterAttached` | `AvaloniaProperty.RegisterAttached<...>` _(not yet verified here)_ |

**Rule of thumb:** control property that participates in styling/binding →
`StyledProperty`. Plain data-only property → prefer a VM `[ObservableProperty]`
(CommunityToolkit.Mvvm works unchanged in Avalonia).

---

## 6. Style ordering & precedence (the trap that cost us)

**Avalonia ranks a locally-set property value ABOVE a style setter — exactly like
WPF.** So this silently breaks:
```xml
<!-- WRONG: local BorderThickness/Brush block the .speaking setters forever -->
<Border x:Name="OuterBorder" BorderThickness="0" BorderBrush="Transparent"
        Classes.speaking="{Binding IsSpeaking}"/>
```
The ring never appears because the inline `BorderThickness="0"` outranks
`Border#OuterBorder.speaking`'s `BorderThickness="3"`.

**Fix — base values in a style, never inline:**
```xml
<Style Selector="Border#OuterBorder">
  <Setter Property="BorderThickness" Value="0"/>
  <Setter Property="BorderBrush" Value="Transparent"/>
  <Setter Property="Background" Value="{StaticResource Tile}"/>
</Style>
<!-- element keeps only structural attrs + class bindings -->
<Border x:Name="OuterBorder" CornerRadius="10" ClipToBounds="True"
        Classes.speaking="{Binding IsSpeaking}"/>
```

Other precedence notes:
- Among **matching styles**, the **later** one wins. Order `.pinned` after
  `.speaking` if pinned should win when both are active (mirrors WPF "last
  DataTrigger wins").
- Style **specificity** does not override source order the way CSS does — think
  "last matching setter wins," not "most specific wins."

---

## 7. Compiled bindings (`x:DataType`) — adopt from day one

Not a WPF concept. Declare the binding's data type; the compiler validates every
`{Binding}` path against it.

```xml
<UserControl xmlns:vm="using:...ViewModels"
             x:DataType="vm:ConferenceTileViewModel">
  ...
  <TextBlock Text="{Binding DisplayName}"/>   <!-- validated at build -->
</UserControl>

<DataTemplate DataType="vm:ConferenceTileViewModel"> ... </DataTemplate>
```
- Typo in a binding path → **build error**, not a silent runtime blank.
- For control-owned (non-VM) properties, use element-name binding so the compiler
  resolves against the element, not the DataContext: `{Binding #Root.SelfSuffix}`.
- To opt a single binding out of compilation: `{Binding X, Mode=…}` still works;
  reflection fallback via `{ReflectionBinding X}` _(not yet needed here)_.

---

## 8. Findings from the ConferenceTile port

**What surprised us**
- Converters largely **evaporate** — `Visibility` becoming a plain `bool IsVisible`
  removed all three of the tile's converters.
- Local-value-beats-style precedence is identical to WPF and is **invisible until
  you look at the rendered pixels** (compiles fine, binds fine, just doesn't apply).

**Thought it'd be hard, wasn't**
- The VM: **zero** logic changes — CommunityToolkit.Mvvm source generators run the
  same. Only one *type* swap (`BitmapSource`→`Bitmap`).
- `<Run>` inline bindings, `x:Double`/`CornerRadius`/`Thickness` resources, and the
  merged dictionary all compiled first try.

**Thought it'd be easy, wasn't**
- `DataTrigger`→classes is a genuine mental-model shift (declarative "when X set Y"
  → "toggle a class, style the class"), not a syntax swap. Budget thinking time.
- Screenshotting from a headless shell needs off-screen rendering (see §9), not
  `screencapture`.

---

## 9. Commands & verification

```bash
# Build a single project (macOS, Apple Silicon)
dotnet build src/ClassroomCtrl.Avalonia.Sandbox/ClassroomCtrl.Avalonia.Sandbox.csproj

# Run the app (opens a window in your desktop session)
dotnet run --project src/ClassroomCtrl.Avalonia.Sandbox

# Build the whole solution
dotnet build ClassroomCtrl.Avalonia.slnx
```

**XAML preview:** the Avalonia previewer runs inside the IDE extensions (VS Code
"Avalonia for VSCode", Rider Avalonia plugin) — it renders the `.axaml` live. There
is no standalone CLI previewer.

**Headless screenshot (no display / permission needed)** — the technique we used
to capture `docs/phase-24.3-conferencetile.png` when `screencapture` failed:
```csharp
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();
var win = new MainWindow(); win.Show();
Dispatcher.UIThread.RunJobs();
win.CaptureRenderedFrame()!.Save("out.png");   // needs Avalonia.Headless + Avalonia.Skia
```
This is now a committed, reusable tool — **`tools/HeadlessCapture/`** (Phase 25.2):
```bash
dotnet run --project tools/HeadlessCapture -- --list                    # scenarios
dotnet run --project tools/HeadlessCapture -- --scenario theme --output x.png
dotnet run --project tools/HeadlessCapture -- --view-type <FQN> --output x.png   # ad-hoc
```
Add a scenario per future port in `tools/HeadlessCapture/Scenarios.cs`. Caveats:
static scenarios are content-deterministic (but PNG/AA cause small byte diffs — diff
**visually**, not by hash); animated captures are timing-variant (headless clock uses
real time) — illustrative, not a pixel-diff gate. See the tool's README.

**Debug tips**
- Binding not showing? First check the **build log** — with `x:DataType` a bad path
  is a compile error. Without it, bindings fail silently.
- Trigger/class not applying? Suspect **local-value precedence** (§6) before
  anything else.
- Set `Background` on a `Panel`/`Border` to make invisible layout regions visible
  while debugging.
- `Classes` are case-sensitive and must match the selector exactly.

---

## 10. Animations (WPF Storyboard → Avalonia Animation)
_(added Phase 25.1-C, from the ConferenceTile reaction float-up port)_

WPF `Storyboard` + `DoubleAnimation`/`...UsingKeyFrames` started via
`BeginAnimation` → Avalonia **`Animation`** (`KeyFrame` / `Cue` / `Setter`) run via
**`await anim.RunAsync(control)`** (or fire-and-forget). `Cue` is normalized time
`0.0–1.0`; `FillMode.Forward` holds the final keyframe values.

```csharp
var fade = new Animation
{
    Duration = TimeSpan.FromSeconds(2.8),
    FillMode = FillMode.Forward,
    Children =
    {
        new KeyFrame { Cue = new Cue(0d),    Setters = { new Setter(Visual.OpacityProperty, 0d) } },
        new KeyFrame { Cue = new Cue(0.15d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
        new KeyFrame { Cue = new Cue(1d),    Setters = { new Setter(Visual.OpacityProperty, 0d) } },
    },
};
_ = fade.RunAsync(myControl);
```

| WPF | Avalonia |
|---|---|
| `Storyboard` | `Animation` |
| `DoubleAnimationUsingKeyFrames` + `LinearDoubleKeyFrame` | `Animation.Children` = `KeyFrame` list |
| `KeyTime.FromPercent(0.15)` | `Cue = new Cue(0.15d)` |
| `FillBehavior=HoldEnd` | `FillMode = FillMode.Forward` |
| `BeginAnimation(prop, anim)` | `anim.RunAsync(control)` (returns a Task) |
| `RepeatBehavior.Forever` | `IterationCount = IterationCount.Infinite` |

**Two gotchas that cost real debug time (code-created animations):**
1. **`RunAsync` targets a `Visual`, not any `Animatable`.** Running it on a bare
   `TranslateTransform` throws `InvalidCastException`. Animate properties **on the
   control**, not on a detached transform object.
2. **`RenderTransform` (translate) needs the `TransformOperationsAnimator`, which is
   `internal` and only auto-registers from the XAML path** — so
   `Setter(RenderTransformProperty, TransformOperations.Parse("translateY(...)"))`
   from code throws *"No animator registered for RenderTransform"* and you can't add
   the animator (it's internal; `Animation.Animators` isn't public either). **Fix:**
   animate a property whose animator is registered by default — e.g. **`Margin`**
   (`ThicknessAnimator`) or a `double` like `Canvas.Top`. For a centered element, a
   `Margin.Top` of `+20 → -80` produces a clean upward "rise" without transforms.
   _(If you author the animation in XAML instead, `RenderTransform`/`translateY`
   works — XAML registers the transform animator for you.)_

> For simple state-driven animations (hover, show/hide), prefer **`Transitions`** on
> a style (`<Style Selector="..."><Style.Animations>` or `Transitions`) over
> code-behind `Animation` — closer to Avalonia's declarative grain. Code-behind
> `RunAsync` fits one-shot, imperatively-triggered effects like this reaction float.

## 11. Popups & flyouts (WPF Popup / ContextMenu → Avalonia Flyout)
_(added Phase 25.1-B, from the ConferenceToolbar reaction picker)_

A WPF `<Popup>` toggled from code-behind (`IsOpen`, `PlacementTarget`,
`StaysOpen=False`, `AllowsTransparency`) → an Avalonia **`Button.Flyout`** /
**`Flyout`**, which gives light-dismiss + placement for free (no code-behind toggle):

```xml
<Button Content="⋮">
  <Button.Flyout>
    <Flyout Placement="Top" ShowMode="Standard">
      <Border ...><!-- content --></Border>
    </Flyout>
  </Button.Flyout>
</Button>
```

| WPF Popup | Avalonia |
|---|---|
| `<Popup IsOpen=... PlacementTarget=...>` + code toggle | `Button.Flyout` (auto-toggles on click) or `FlyoutBase.AttachedFlyout` + `ShowAttachedFlyout` |
| `StaysOpen="False"` (light dismiss) | default for `Flyout` (`ShowMode="Standard"`) |
| `Placement="Top"` / `VerticalOffset` | `Placement="Top"` (+ `HorizontalOffset`/`VerticalOffset` if needed) |
| `ContextMenu` | `MenuFlyout` (items) or `ContextFlyout` |
| `AllowsTransparency="True"` | not needed — flyouts are transparent-capable |

Notes / caveats:
- Reaction buttons: WPF used `Click`+`Tag`+reflection to resolve the command off the
  host DataContext; in Avalonia bind `Command` + `CommandParameter` directly.
- Placement/offset/dismiss behavior renders in a **separate popup layer** that the
  headless-Skia harness can't reliably screenshot — verify flyout positioning by
  running the app interactively, not from a headless capture.
- For `StudentCard`'s large `ContextMenu` (flagged complex in the 24.3 findings),
  the target is `MenuFlyout`/`ContextFlyout` with `MenuItem`s; the WPF
  `PlacementTarget.Tag`/`RelativeSource AncestorType=ContextMenu` command-routing
  will need rethinking (Avalonia menu items bind against the flyout's DataContext).

### `ContextMenu` specifics (Phase 25.4 — ported StudentCard's 17-item menu)
- **Right-click gesture is built in.** Set `<Border.ContextMenu><ContextMenu>…` (or
  `ContextFlyout`) and Avalonia opens it on right-click — **no code-behind** (WPF
  needed the `Tag`-stash + placement handling). This is *simpler* than WPF.
- **Command routing:** menu items live in a popup, so `$parent` fails — route via the
  target's DataContext (`Host` pattern, see §13). WPF `PlacementTarget.Tag.X` → `Host.X`.
- **Structure ports 1:1:** `<MenuItem Header="…" Command=… CommandParameter=…/>`,
  `<Separator/>`, and **nested `<MenuItem>`** for cascading submenus — the cascade
  chevron ▸ + hover-open behavior render correctly (verified).
- **Headless capture:** the **top-level `ContextMenu` popup renders** into
  `CaptureRenderedFrame` (open via `contextMenu.Open(target)` first) — unlike a
  `Button.Flyout`. But a **nested submenu popup does NOT rasterize** into the frame
  (set `menuItem.IsSubMenuOpen = true`; the parent highlights ▸ but the child items
  don't appear). Verify dynamic submenu *contents* functionally, not by screenshot.

### Dynamic submenu — `ItemsSource` + `ItemContainerTheme` (Phase 25.4-E)
WPF customizes generated items with **`ItemContainerStyle`** (a `Style`); Avalonia uses
**`ItemContainerTheme`** (a `ControlTheme`). This applies to any items host —
`MenuItem`, `ListBox`, `ItemsControl`, `TreeView`, etc.

```xml
<!-- WPF -->
<MenuItem Header="Assign to Room" ItemsSource="{Binding Rooms}">
  <MenuItem.ItemContainerStyle>
    <Style TargetType="MenuItem">
      <Setter Property="Header"  Value="{Binding RoomName}"/>
      <Setter Property="Command" Value="{Binding DataContext.AssignCmd, RelativeSource=…}"/>
      <Setter Property="CommandParameter" Value="{Binding}"/>
    </Style>
  </MenuItem.ItemContainerStyle>
</MenuItem>

<!-- Avalonia -->
<MenuItem Header="Assign to room"
          ItemsSource="{Binding Host.Rooms}"
          IsEnabled="{Binding Host.HasRooms}">          <!-- empty-state -->
  <MenuItem.ItemContainerTheme>
    <ControlTheme TargetType="MenuItem" x:DataType="vm:RoomDemo">   <!-- x:DataType → compiled bindings -->
      <Setter Property="Header"  Value="{Binding RoomName}"/>
      <Setter Property="Command" Value="{Binding Host.AssignToRoomCommand}"/>  <!-- Host on the item -->
      <Setter Property="CommandParameter" Value="{Binding}"/>       <!-- the room -->
    </ControlTheme>
  </MenuItem.ItemContainerTheme>
</MenuItem>
```
| WPF | Avalonia |
|---|---|
| `ItemContainerStyle` (a `Style`) | **`ItemContainerTheme`** (a `ControlTheme`) |
| Setters set `Header`/`Command`/`CommandParameter` | **same** |
| (no compile-time check) | add **`x:DataType`** on the `ControlTheme` for compiled bindings |
| command via `RelativeSource`/`Tag` | via the item's **`Host`** back-ref (§13), popup-safe |

Empty-state: bind the parent's `IsEnabled` to a `HasItems`-style flag (raise change
notification on the collection). Verified: 3 rooms → 3 realized items, command fires;
`Rooms.Clear()` → parent disables.

## 12. Manual tabs vs `TabControl`
_(added Phase 25.3, from the ConferenceSidebar port)_

Avalonia has a full `TabControl` (`<TabControl><TabItem Header="…">…</TabItem></TabControl>`,
styleable via `ControlTheme`). But the shipped ConferenceSidebar — and this port —
**hand-rolls** its Chat/Participants tabs: toggle `Button`s + `IsVisible`-gated body
panels driven by VM bools.

```xml
<!-- tab buttons: SidebarTabPill ControlTheme + .active class -->
<Button Classes="tab" Theme="{StaticResource SidebarTabPill}" Content="Chat"
        Command="{Binding SelectConferenceChatTabCommand}"
        Classes.active="{Binding IsConferenceChatTabSelected}"/>
<!-- bodies: one IsVisible-gated panel per tab -->
<DockPanel IsVisible="{Binding IsConferenceChatTabSelected}"> … </DockPanel>
<Grid     IsVisible="{Binding IsConferenceParticipantsTabSelected}"> … </Grid>
```

| Hand-roll (buttons + `IsVisible`) when… | Use real `TabControl` when… |
|---|---|
| Custom tab chrome (pill/underline) that doesn't match the default template | Standard tab look/UX is fine |
| Few tabs; selected state already lives as VM bools + commands | Many tabs; want built-in selection mgmt |
| Bodies are arbitrary panels you fully control | Want keyboard nav / `TabStripPlacement` for free |

**Sidebar rationale:** faithful to the shipped WPF (which hand-rolls), and the
pill/underline design is trivial as classes; matching it inside a `TabControl` would
need a `ControlTemplate` override — more work for a 2-tab surface. Skipping
`TabControl` was the right call *here*; it isn't a blanket rule.

## 13. Advanced binding scopes (`$parent`, element-name)
_(added Phase 25.3 — the key idiom for reaching a host VM from inside an item template)_

`$parent[Type]` is the Avalonia equivalent of WPF
`RelativeSource={RelativeSource AncestorType=Type}` (FindAncestor): it walks up to
the nearest ancestor of `Type`.

```xml
<!-- WPF: {Binding SomeProp, RelativeSource={RelativeSource AncestorType=UserControl}} -->
{Binding $parent[UserControl].SomeControlProperty}
```

**CRITICAL for compiled bindings (`x:DataType`):** inside a `DataTemplate` whose
`x:DataType` is the *item* type, reaching the *ancestor's* `DataContext` members
needs an explicit **cast** so the compiler can resolve them — the ancestor's
`DataContext` is statically `object`:

```xml
<!-- item template x:DataType is ParticipantDemo; reach the host VM's command/role -->
<Button Content="Mute"
        IsVisible="{Binding $parent[UserControl].((vm:ConferenceSidebarDemoViewModel)DataContext).Role.CanMuteOthers}"
        Command="{Binding  $parent[UserControl].((vm:ConferenceSidebarDemoViewModel)DataContext).MuteParticipantCommand}"
        CommandParameter="{Binding}"/>   <!-- the item itself -->
```
Without the `((vm:HostVm)DataContext)` cast you get a compile error (can't bind
`.Role`/`.Command` on `object`). WPF didn't need this because its bindings are
late-bound/reflection-based.

Related scope selectors:
- `#Name.Property` — element-name binding (WPF `ElementName=`); reach a named control.
- `$self` — the control itself; `$parent` — the immediate parent (no type filter).
- Reflection fallback: `{ReflectionBinding …}` skips compile-time checking if you
  ever need the WPF-style late binding.

### ⚠️ `$parent` does NOT cross a popup/ContextMenu boundary
_(Phase 25.4 — verified empirically with a headless route-test)_

**`$parent[…]` ancestor-walk stops at the popup root.** A `ContextMenu`/`Flyout`/
`MenuFlyout` renders in a **separate visual root**, so from inside a menu item
`$parent[ItemsControl]` / `$parent[Window]` resolves to **nothing** → the bound
`Command` comes back **null** (menu item silently disabled). This is the opposite of
WPF, whose ContextMenu used `PlacementTarget` to walk back to the owner.

**The fix — route via the target's DataContext.** An Avalonia `ContextMenu` *inherits
the DataContext of the control it's attached to* (for `StudentCard` that's the item
VM). So give the item a **`Host` back-reference** and bind through it — a plain
DataContext binding that lives entirely inside the popup's own scope:

```xml
<!-- item VM exposes: public HostVm? Host {get;set;}  (wired when the item is created) -->
<MenuItem Header="Lock"
          Command="{Binding Host.LockOneCommand}"
          CommandParameter="{Binding}"/>   <!-- {Binding} = the item itself -->
```
Verified: flat item AND nested-submenu item both resolve non-null and fire with the
correct parameter. (For controls in the **main tree** — e.g. card-body buttons —
`$parent[…]` still works; only the popup boundary breaks it. Standardize on the
`Host` pattern so menu and body use one idiom.)

WPF `PlacementTarget.Tag.XCommand` (Tag-stash hack) → Avalonia `Host.XCommand`.

**Nuance (Phase 25.4-E — verified):** `$parent` traverses the **menu's OWN hierarchy**
even across nested submenu popups — a generated submenu item CAN reach its parent
`MenuItem` via `$parent[MenuItem]`:
```xml
<!-- inside a dynamic submenu's ItemContainerTheme; reaches the parent item's student -->
{Binding $parent[MenuItem].((vm:StudentCardDemoViewModel)DataContext).DisplayName}  <!-- resolved "Somchai" ✓ -->
```
So the precise rule is: **`$parent` walks the menu/popup's internal ancestor chain,
but cannot escape the popup outward to the host window's tree** (that outward hop is
what returns null). Reaching a *parent menu item* = OK; reaching the *hosting
ItemsControl/Window* = use `Host`.

## 14. Icon strategy: NONE NEEDED
_(Phase 25.5 — established by repo research, not assumption)_

The shipped Windows codebase already uses **cross-platform icons** — **emoji +
Unicode symbols**, **not** Segoe MDL2 or an icon font/library. (`MaterialDesignThemes`
is referenced for the *theme*, not icons; `PackIcon`/Segoe-MDL2 usages = **0**.) These
render **natively on macOS** via Apple Color Emoji + system fonts. **No icon system,
font, or library is needed.**

Verified across Phase 24.3–25.5 captures: 👑 🎤 👁 ✓ ✕ ⋮ ➤ 📎 🎙 🔒 🛡 ⚙ all render
correctly on macOS out-of-the-box. Unicode symbols also accept a `Foreground` tint
(e.g. ✓ green, ✕ red, ➤ blue); color emoji ignore `Foreground` (they carry their own).

**IF future custom glyphs are ever needed:**
- **SVG** via `PathIcon`/`Image` — for one-off bespoke designs.
- **`Material.Icons.Avalonia`** — for a comprehensive set.

But adopting either **now would be unwarranted** — save the dependency.

**Historical note:** the WPF codebase had a PNG reaction-emoji fallback for a
**Windows-11-Thai-locale font-shaping bug**. macOS doesn't have that issue — the PNG
assets should **NOT** be ported.

## 15. Multiple theme dictionaries coexisting
_(Phase 25.5 — ConferenceDarkTheme + ClassroomLightTheme side by side)_

Two ported token sets live in `App.axaml` at once with **no collision** because their
key namespaces differ: `Conf*` (Conference dark) vs `Surface.*`/`Border.*`/`Accent.*`/
`Text.*`/`Status.*` (Classroom light). Merge both:
```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceInclude Source="avares://Asm/Themes/ConferenceDarkTheme.axaml"/>
      <ResourceInclude Source="avares://Asm/Themes/ClassroomLightTheme.axaml"/>
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```
- Port the WPF **color-token dicts** (`<Color>` + `<SolidColorBrush Color="{StaticResource X.Color}">`) ~1:1; keep key names verbatim for cross-repo search + DynamicResource-swap parity.
- **Don't** port WPF `Style`s that override **MaterialDesign controls** (Buttons/Inputs/
  DataDisplay) — Avalonia re-themes controls via its own `ControlTheme`s/`FluentTheme`;
  that's a separate re-authoring effort, not a resource-copy.
- Runtime light/dark swap (the WPF `DynamicResource` + Colors.Light/Dark pair) is a
  separate feature (theme manager) — not required just to *consume* the tokens.

## 16. Light-surface views in a dark-default app — `ThemeVariantScope`
_(Phase 25.6 — porting a Classroom light dialog while the app root is Dark)_

If the app sets `RequestedThemeVariant="Dark"` (for our Conference tabs) but a view
is a **light** surface (Classroom dialogs), Fluent's **variant-aware standard controls**
(`CheckBox`/`ComboBox`/`TextBox`/`ScrollBar`) render in *dark* mode → light text on your
light surface = unreadable. Wrap the light view in a **`ThemeVariantScope`**:
```xml
<ThemeVariantScope RequestedThemeVariant="Light">
  <Border Background="{StaticResource Surface.Elevated}"> … light dialog … </Border>
</ThemeVariantScope>
```
Now Fluent controls inside render their **light** variant (dark text, light fills),
congruent with the Classroom surface. Your explicit `{StaticResource Surface.*/Text.*}`
brushes are variant-agnostic and unaffected. (Exact accent-color match of Fluent
controls to the Classroom palette is a separate "control theming" polish.)

**Design-system reuse (this port applied, didn't invent):** WPF keyed button `Style`s →
Avalonia **`ControlTheme`s** applied via `Theme="{StaticResource Button.X}"` (§3e); WPF
keyed `TextBlock` `Style`s (`Text.H*`) → **style classes** `Classes="h1"` (§3). Both live
in `Themes/ClassroomControls.axaml` (a `<Styles>` file: ControlThemes in `Styles.Resources`
+ the text-class `Style`s), included via `StyleInclude`.

## 17. Reference links

- Avalonia docs: https://docs.avaloniaui.net
- WPF → Avalonia migration: https://docs.avaloniaui.net/docs/get-started/wpf/
- Styles & selectors: https://docs.avaloniaui.net/docs/styling/
- Data binding / compiled bindings: https://docs.avaloniaui.net/docs/basics/data/data-binding/
- Built-in converters (`ObjectConverters`, `StringConverters`, `BoolConverters`):
  https://docs.avaloniaui.net/docs/guides/data-binding/how-to-use-value-converters
- Headless testing/rendering: https://docs.avaloniaui.net/docs/concepts/headless/

---
_Living document (17 sections) — extend as later ports surface new patterns. Covered:
triggers→classes, converters, DP→StyledProperty, precedence, compiled bindings,
keyed-Style→ControlTheme + ControlTemplate/pseudo-classes (§3e), animations (§10),
popups/flyouts + ContextMenu + dynamic ItemsSource submenu / ItemContainerTheme (§11),
manual-tabs vs TabControl (§12), advanced binding scopes / $parent + compiled-binding
cast + popup-boundary routing (§13), icon strategy = none-needed (§14), multiple theme
dictionaries coexisting (§15). Still uncovered: complex ControlTemplates re-authoring
(MaterialDesign control styles → Avalonia ControlThemes), DynamicResource theme-swap._
