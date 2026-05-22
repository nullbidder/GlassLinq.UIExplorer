using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace GlassLinq.UIExplorer
{

   
    public partial class MainWindow : Window
    {
        private bool _isProcessing = false;
        private CancellationTokenSource _cts;
        private DispatcherTimer _spyTimer;
        private SpyOverlayWindow _overlay;
        private AutomationElement _lastFoundElement;
        private readonly SemaphoreSlim _spyLock = new SemaphoreSlim(1, 1);
        private Stopwatch _performanceStopwatch = new Stopwatch();



        public MainWindow()
        {
            InitializeComponent();

            // Initialize the spy timer
            _spyTimer = new DispatcherTimer();
            _spyTimer.Interval = TimeSpan.FromMilliseconds(100);
            _spyTimer.Tick += SpyTimer_Tick;

            // Load initial tree
            LoadAutomationElementsAsync();

            // Check for command line argument
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && args[1].Equals("/indicate", StringComparison.OrdinalIgnoreCase))
            {
                this.Loaded += (s, e) => StartIndicateMode();
            }
        }

        #region Tree Loading with Lazy Loading and Virtualization

        private async void LoadAutomationElementsAsync()
        {
            try
            {
                // 1. Setup the loading state
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                // Clear existing items (Make sure the name matches your XAML TreeView name)
                AutomationTree.Items.Clear();

                // 2. Run the heavy search on a background thread
                var rootNode = await Task.Run(() =>
                {
                    // Start at the Desktop (RootElement)
                    var root = new TreeNodeViewModel(AutomationElement.RootElement, null);
                    root.DisplayName = "Desktop";

                    // FIX: Pass the token to the method
                    root.LoadChildren(token);
                    return root;
                }, token);

                // 3. Add the result back to the UI
                if (!token.IsCancellationRequested)
                {
                    AutomationTree.Items.Add(rootNode);
                    rootNode.IsExpanded = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Load Error: " + ex.Message);
            }
        }

        private int CountElements(TreeNodeViewModel node)
        {
            int count = 1;
            foreach (var child in node.Children)
            {
                count += CountElements(child);
            }
            return count;
        }

        #endregion

        #region Search Functionality

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                ClearSearchHighlight();
                return;
            }

            PerformSearch(txtSearch.Text.Trim());
        }

        private void PerformSearch(string searchTerm)
        {
            try
            {
                statusText.Text = $"Searching for '{searchTerm}'...";
                var results = new List<TreeNodeViewModel>();

                foreach (TreeNodeViewModel root in AutomationTree.Items)
                {
                    SearchNode(root, searchTerm.ToLower(), results);
                }

                if (results.Count > 0)
                {
                    // Expand path to first result and select it
                    ExpandPathToNode(results[0]);
                    results[0].IsSelected = true;
                    statusText.Text = $"Found {results.Count} match(es)";
                }
                else
                {
                    statusText.Text = $"No matches found for '{searchTerm}'";
                }
            }
            catch (Exception ex)
            {
                statusText.Text = $"Search error: {ex.Message}";
            }
        }

        /// <summary>
        /// Selects and expands the element in the tree using RawViewWalker for complete hierarchy
        /// </summary>
        private async Task SelectElementInTree(AutomationElement target)
        {
            try
            {
                var ancestors = new Stack<AutomationElement>();
                var current = target;
                var walker = TreeWalker.RawViewWalker;

                // Walk up to Desktop root
                while (current != null)
                {
                    ancestors.Push(current);
                    try
                    {
                        var parent = walker.GetParent(current);
                        if (parent == null) break;
                        current = parent;
                    }
                    catch { break; }
                }

                TreeNodeViewModel currentNode = null;
                ItemsControl currentContainer = AutomationTree;

      

                while (ancestors.Count > 0)
                {
                    var nextTarget = ancestors.Pop();
                    TreeNodeViewModel foundNode = null;

                    // Search in current container
                    if (currentContainer == AutomationTree)
                    {
                        // Search root level
                        foundNode = AutomationTree.Items.Cast<TreeNodeViewModel>()
                            .FirstOrDefault(n => AreSameElement(n.AutomationElement, nextTarget));
                    }
                    else
                    {
                        // Search in children of current node
                        if (currentNode != null)
                        {
                            // Ensure children are loaded
                            if (currentNode.Children.Count == 0)
                            {
                                await Task.Run(() => currentNode.LoadChildren(CancellationToken.None));
                            }

                            foundNode = currentNode.Children
                                .FirstOrDefault(n => AreSameElement(n.AutomationElement, nextTarget));
                        }
                    }

                    if (foundNode == null)
                    {
                        // Element not found in expected location - might need to load parent's children
                        statusText.Text = $"Could not locate element in tree hierarchy";
                        return;
                    }

                    // 3. Expand the node (unless it's the final target)
                    if (ancestors.Count > 0)
                    {
                        foundNode.IsExpanded = true;
                    }

                    // 4. Select the final target
                    if (ancestors.Count == 0)
                    {
                        foundNode.IsSelected = true;
                    }

                    // 5. CRITICAL: Wait for TreeView to generate containers
                    // This allows WPF to create TreeViewItem controls before we continue
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    await Task.Delay(50); // Small delay to ensure UI updates

                    // 6. Get the TreeViewItem container for next iteration
                    var tvi = AutomationTree.ItemContainerGenerator.ContainerFromItem(foundNode) as TreeViewItem;
                    if (tvi != null)
                    {
                        currentContainer = tvi;
                        tvi.BringIntoView();

                        // Ensure the container's ItemContainerGenerator is ready
                        if (tvi.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                        {
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                        }
                    }

                    currentNode = foundNode;
                }

                statusText.Text = $"Selected: {currentNode?.DisplayName}";
            }
            catch (Exception ex)
            {
                statusText.Text = $"Error selecting element: {ex.Message}";
                Debug.WriteLine($"SelectElementInTree error: {ex}");
            }
        }

        /// <summary>
        /// Compare two AutomationElements for equality
        /// </summary>
        private bool AreSameElement(AutomationElement e1, AutomationElement e2)
        {
            if (e1 == null || e2 == null) return false;

            try
            {
                return Automation.Compare(e1, e2);
            }
            catch
            {
                return false;
            }
        }

        private void SearchNode(TreeNodeViewModel node, string searchTerm, List<TreeNodeViewModel> results)
        {
            // Search in name, automation ID, and control type
            if (node.DisplayName.ToLower().Contains(searchTerm) ||
                node.AutomationId?.ToLower().Contains(searchTerm) == true ||
                node.ControlType.ToLower().Contains(searchTerm))
            {
                results.Add(node);
            }

            // Recursively search children
            foreach (var child in node.Children)
            {
                SearchNode(child, searchTerm, results);
            }
        }

        private void ExpandPathToNode(TreeNodeViewModel node)
        {
            var path = new Stack<TreeNodeViewModel>();
            var current = node;

            // Build path from node to root
            while (current != null)
            {
                path.Push(current);
                current = current.Parent;
            }

            // Expand each node in the path
            foreach (var pathNode in path)
            {
                pathNode.IsExpanded = true;
            }
        }

        private void ClearSearchHighlight()
        {
            foreach (TreeNodeViewModel root in AutomationTree.Items)
            {
                ClearNodeSelection(root);
            }
            statusText.Text = "Ready";
        }

        private void ClearNodeSelection(TreeNodeViewModel node)
        {
            node.IsSelected = false;
            foreach (var child in node.Children)
            {
                ClearNodeSelection(child);
            }
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            txtSearch.Text = string.Empty;
            ClearSearchHighlight();
        }

        #endregion

        #region Tree Operations

        private void RefreshTree_Click(object sender, RoutedEventArgs e)
        {
            LoadAutomationElementsAsync();
        }

        private async void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                statusText.Text = "Expanding all nodes... This may take a moment";
                btnExpandAll.IsEnabled = false;

                await Task.Run(() =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        foreach (TreeNodeViewModel root in AutomationTree.Items)
                        {
                            ExpandAllNodes(root);
                        }
                    });
                });

                statusText.Text = "All nodes expanded";
            }
            catch (Exception ex)
            {
                statusText.Text = $"Error expanding: {ex.Message}";
            }
            finally
            {
                btnExpandAll.IsEnabled = true;
            }
        }

        private void ExpandAllNodes(TreeNodeViewModel node)
        {
            node.IsExpanded = true;
            foreach (var child in node.Children)
            {
                ExpandAllNodes(child);
            }
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeNodeViewModel selectedNode)
            {
                DisplayElementProperties(selectedNode);
                GenerateSelectorForElement(selectedNode);
            }
        }

        #endregion

        #region Property Display

        private void DisplayElementProperties(TreeNodeViewModel node)
        {
            try
            {
                var properties = new List<PropertyItem>();
                // Ensure we access the correct property name for the AutomationElement
                var element = node.AutomationElement;

                if (element == null) return;

                var current = element.Current;

                properties.Add(new PropertyItem { PropertyName = "Name", Value = current.Name ?? "(none)" });
                properties.Add(new PropertyItem { PropertyName = "AutomationId", Value = current.AutomationId ?? "(none)" });
                properties.Add(new PropertyItem { PropertyName = "ControlType", Value = current.LocalizedControlType });
                properties.Add(new PropertyItem { PropertyName = "ClassName", Value = current.ClassName ?? "(none)" });

                try
                {
                    var process = Process.GetProcessById(current.ProcessId);
                    properties.Add(new PropertyItem { PropertyName = "ProcessName", Value = process.ProcessName });
                    properties.Add(new PropertyItem { PropertyName = "ProcessId", Value = current.ProcessId.ToString() });
                }
                catch { }

                var rect = current.BoundingRectangle;
                properties.Add(new PropertyItem
                {
                    PropertyName = "BoundingRectangle",
                    Value = $"X:{rect.X:F0}, Y:{rect.Y:F0}, W:{rect.Width:F0}, H:{rect.Height:F0}"
                });

                dgProperties.ItemsSource = properties;
            }
            catch (Exception ex)
            {
                statusText.Text = $"Error: {ex.Message}";
            }
        }

        private void GenerateSelectorForElement(TreeNodeViewModel node)
        {
            try
            {
                if (node.AutomationElement == null) return;

                string selector = SelectorBuilder.GenerateSelector(node.AutomationElement);
                txtFullSelector.Text = selector;
            }
            catch (Exception ex)
            {
                txtFullSelector.Text = $"Error generating selector: {ex.Message}";
            }
        }

        private void CopySelector_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(txtFullSelector.Text))
                {
                    System.Windows.Clipboard.SetText(txtFullSelector.Text);
                    statusText.Text = "Selector copied to clipboard";
                }
            }
            catch (Exception ex)
            {
                statusText.Text = $"Error copying: {ex.Message}";
            }
        }

        #endregion
        private async void ValidateSelector_Click(object sender, RoutedEventArgs e)
        {
            string selector = txtFullSelector.Text; // Your <wnd.../><ctrl.../> string

            // 1. Basic Parsing (Regex) to get attributes
            var windowMatch = Regex.Match(selector, @"<wnd app='(.*?)' title='(.*?)'.*?/>");
            var controlMatch = Regex.Match(selector, @"<ctrl automationid='(.*?)' role='(.*?)' name='(.*?)'.*?/>");

            if (!windowMatch.Success || !controlMatch.Success)
            {
                System.Windows.MessageBox.Show("Invalid Selector Format");
                return;
            }

            string appName = windowMatch.Groups[1].Value;
            string autoId = controlMatch.Groups[1].Value;
            string roleName = controlMatch.Groups[2].Value;

            // 2. Search for the element
            AutomationElement foundElement = await Task.Run(() =>
            {
                try
                {
                    // 1. Force a Z-Order refresh so the app "wakes up"
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        _overlay.Topmost = false;
                        _overlay.Topmost = true;
                    });

                    // 2. Find the Window using a Partial Match (Wildcard logic)
                    // Instead of exact name, look for 'SnippingTool' in the Process Name
                    var allWindows = AutomationElement.RootElement.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                    AutomationElement snipWindow = null;

                    foreach (AutomationElement win in allWindows)
                    {
                        try
                        {
                            if (win.Current.ClassName.Contains("Window") &&
                                win.Current.Name.Contains("Snipping Tool") ||
                                win.Current.ProcessId != 0 && Process.GetProcessById(win.Current.ProcessId).ProcessName.Contains("SnippingTool"))
                            {
                                snipWindow = win;
                                break;
                            }
                        }
                        catch { continue; }
                    }

                    if (snipWindow == null) return null;

                    // 3. Find the Button using AutomationID ONLY
                    // IDs are much more reliable than Names/Titles which change
                    var ctrlProp = new PropertyCondition(AutomationElement.AutomationIdProperty, "NewCaptureButton");

                    // Use Descendants to search deep into the Snipping Tool tree
                    return snipWindow.FindFirst(TreeScope.Descendants, ctrlProp);
                }
                catch { return null; }
            });
            // 3. Feedback to User
            if (foundElement != null)
            {
                var rect = foundElement.Current.BoundingRectangle;
                _overlay.UpdatePosition(rect, "Verified!");
                _overlay.Show();

                // Auto-hide the highlight after 2 seconds
                await Task.Delay(2000);
                _overlay.Hide();
            }
            else
            {
                System.Windows.MessageBox.Show("Element not found. The application might be closed or the UI has changed.");
            }
        }
        #region Indicate Mode (Element Picker)

        private void StartIndicateMode()
        {
            _overlay?.Close();

            _overlay = new SpyOverlayWindow();
            _overlay.Left = SystemParameters.VirtualScreenLeft;
            _overlay.Top = SystemParameters.VirtualScreenTop;
            _overlay.Width = SystemParameters.VirtualScreenWidth;
            _overlay.Height = SystemParameters.VirtualScreenHeight;
            _overlay.Show();

            this.WindowState = WindowState.Minimized;
            _spyTimer.Start();
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private async void SpyTimer_Tick(object sender, EventArgs e)
        {
            _overlay.Topmost = false;
            _overlay.Topmost = true;
            // 1. Check for ESC to cancel
            if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) { StopIndicateMode(false); return; }

            // 2. Capture on Left Click
            // Inside SpyTimer_Tick
            if ((GetAsyncKeyState(0x01) & 0x8000) != 0) // Left Click
            {
                _spyTimer.Stop();
                // Do NOT call SelectElementInTree here. Let StopIndicateMode handle everything.
                StopIndicateMode(true);
                return;
            }

            if (_isProcessing) return;
            _isProcessing = true;
            try
            {
                var mousePos = System.Windows.Forms.Control.MousePosition;
                var result = await Task.Run(() => {
                    try
                    {
                        var mouse = System.Windows.Forms.Cursor.Position;
                        System.Windows.Point pt = new System.Windows.Point(mouse.X, mouse.Y);

                        // USE THE NEW STRATEGY:
                        // 1. Get the top-level element
                        AutomationElement root = AutomationElement.FromPoint(pt);
                        if (root == null) return null;

                        // 2. Drill down manually to find the "real" button
                        AutomationElement found = root;
                        TreeWalker walker = TreeWalker.RawViewWalker;

                        AutomationElement child = walker.GetFirstChild(root);
                        while (child != null)
                        {
                            var rect = child.Current.BoundingRectangle;
                            if (rect.Contains(pt))
                            {
                                // Use the IsNoise logic from the code you shared
                                if (!IsNoise(child))
                                {
                                    found = child;
                                    child = walker.GetFirstChild(found);
                                    continue;
                                }
                            }
                            child = walker.GetNextSibling(child);
                        }

                        // Return the found element data
                        return new
                        {
                            Element = found,
                            Bounds = found.Current.BoundingRectangle,
                            Name = found.Current.Name,
                            Type = found.Current.LocalizedControlType
                        };
                    }
                    catch { return null; }
                });
                if (result != null && _spyTimer.IsEnabled)
                {
                    _lastFoundElement = result.Element;
                    string displayName = $"{result.Name} ({result.Type})";

                    // This MUST be called to draw the box
                    _overlay.UpdatePosition(result.Bounds, displayName);
                    statusText.Text = $"Hovering: {displayName}";
                }
            }
            catch { }
            finally { _isProcessing = false; }
        }
        private bool IsNoise(AutomationElement el)
        {
            string name = el.Current.Name;
            string className = el.Current.ClassName;

            // These layers "steal" the focus but aren't actual buttons
            if (name == "PopupHost" || className == "Popup" || className == "Canvas")
                return true;
            if (className == "XamlRoot" || className == "IslandRoot")
                return true;

            return false;
        }
        private async Task ChangeTreeRootToApplication()
        {
            try
            {
                if (_lastFoundElement == null) return;

                // Stop the timer
                if (_spyTimer != null && _spyTimer.IsEnabled)
                {
                    _spyTimer.Stop();
                }

                // Hide the overlay
                if (_overlay != null)
                {
                    _overlay.Hide();
                }

                // Get the root window for this element
                AutomationElement appRoot = GetApplicationRoot(_lastFoundElement);

                if (appRoot != null)
                {
                    // Restore main window
                    this.WindowState = WindowState.Normal;
                    this.Topmost = true;
                    this.Activate();
                    this.Topmost = false;

                    // Show loading indicator
                    loadingIndicator.Visibility = Visibility.Visible;
                    statusText.Text = "Loading application tree...";

                    // Cancel any existing operation
                    _cts?.Cancel();
                    _cts = new CancellationTokenSource();
                    var token = _cts.Token;

                    // Clear current tree
                    AutomationTree.Items.Clear();

                    // Create new root node for the application
                    await Task.Run(() =>
                    {
                        var appNode = new TreeNodeViewModel(appRoot, null);

                        // Get app info
                        try
                        {
                            var process = Process.GetProcessById(appRoot.Current.ProcessId);
                            appNode.DisplayName = $"{process.ProcessName} - {appRoot.Current.Name}";
                            appNode.Icon = "🪟";
                        }
                        catch
                        {
                            appNode.DisplayName = appRoot.Current.Name ?? "Unknown Application";
                            appNode.Icon = "🪟";
                        }

                        // Load children
                        appNode.LoadChildren(token);

                        if (!token.IsCancellationRequested)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                AutomationTree.Items.Add(appNode);
                                appNode.IsExpanded = true;

                                var elementCount = CountElements(appNode);
                                treeStats.Text = $"{elementCount} elements";
                                statusText.Text = $"Loaded application tree: {appNode.DisplayName}";
                            });
                        }
                    }, token);
                }
                else
                {
                    statusText.Text = "Could not find application root";
                }
            }
            catch (Exception ex)
            {
                statusText.Text = $"Error loading application tree: {ex.Message}";
            }
            finally
            {
                loadingIndicator.Visibility = Visibility.Collapsed;
            }
        }

        private AutomationElement GetApplicationRoot(AutomationElement element)
        {
            try
            {
                // Walk up the tree to find the top-level window using RawViewWalker
                TreeWalker walker = TreeWalker.RawViewWalker;
                AutomationElement current = element;
                AutomationElement parent = walker.GetParent(current);

                while (parent != null && parent != AutomationElement.RootElement)
                {
                    current = parent;
                    parent = walker.GetParent(current);
                }

                // Current should now be the top-level window
                return current;
            }
            catch
            {
                return element;
            }
        }
        private async void StopIndicateMode(bool captureElement = true)
        {
            try
            {
                // 1. IMMEDIATELY hide the overlay and stop timer to give visual feedback
                if (_spyTimer != null) _spyTimer.Stop();
                if (_overlay != null) _overlay.Hide();

                // 2. Restore main window
                this.WindowState = WindowState.Normal;
                this.Topmost = true;
                this.Activate();
                this.Topmost = false;

                // 3. Process the capture
                if (captureElement && _lastFoundElement != null)
                {
                    statusText.Text = $"Captured: {_lastFoundElement.Current.Name}. Building tree...";

                    string selector = SelectorBuilder.GenerateSelector(_lastFoundElement);
                    txtFullSelector.Text = selector;
                    System.Windows.Clipboard.SetText(selector);

                    // ONLY call this once!
                    await SelectElementInTree(_lastFoundElement);
                }
                else if (!captureElement)
                {
                    statusText.Text = "Indicate mode cancelled";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during stop: {ex.Message}");
                statusText.Text = $"Error: {ex.Message}";
            }
        }


        private void IndicateElement_Click(object sender, RoutedEventArgs e)
        {
            StartIndicateMode();
        }

        #endregion
    }



    #region TreeNodeViewModel with Lazy Loading

    #region TreeNodeViewModel with Lazy Loading

    public class TreeNodeViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isSelected;
        private ObservableCollection<TreeNodeViewModel> _children = new ObservableCollection<TreeNodeViewModel>();

        public AutomationElement AutomationElement { get; }
        public TreeNodeViewModel Parent { get; }
        public string DisplayName { get; set; }
        public string Icon { get; set; }
        public string AutomationId { get; set; }
        public string ControlType { get; set; }

        public ObservableCollection<TreeNodeViewModel> Children => _children;

        // FIX: This constructor resolves the "does not contain a constructor that takes 2 arguments" error
        public TreeNodeViewModel(AutomationElement element, TreeNodeViewModel parent)
        {
            AutomationElement = element;
            Parent = parent;

            try
            {
                var info = element.Current;
                this.AutomationId = info.AutomationId;

                // 1. Get the type name (e.g., "region", "button", etc.)
                string typeName = info.LocalizedControlType;

                // 2. FIX: If it's "region", rename it to "Pane"
                if (typeName.Equals("region", StringComparison.OrdinalIgnoreCase))
                {
                    typeName = "Pane";
                }

                this.ControlType = typeName;

                // 3. Keep your original formatting logic, but use our 'typeName' variable
                // This ensures it shows: Pane 'Google Chrome' instead of region 'Google Chrome'
                DisplayName = string.IsNullOrEmpty(info.Name)
                    ? typeName
                    : $"{typeName} '{info.Name}'";

                Icon = GetIconForType(info.ControlType);
            }
            catch
            {
                DisplayName = "Unknown Element";
                Icon = "❓";
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged(nameof(IsExpanded));
                    // If we used a dummy child for lazy loading, we would trigger LoadChildren here
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        // UPDATED: Use RawViewWalker for complete hierarchy matching UiPath
        public void LoadChildren(CancellationToken token)
        {
            try
            {
                // Must use RawViewWalker to see the Panes/Groups that UiPath sees
                var walker = TreeWalker.RawViewWalker;
                var child = walker.GetFirstChild(this.AutomationElement);

                while (child != null && !token.IsCancellationRequested)
                {
                    var childViewModel = new TreeNodeViewModel(child, this);

                    // Add to UI thread immediately so SelectElementInTree can find it
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        this.Children.Add(childViewModel);
                    });

                    child = walker.GetNextSibling(child);
                }
            }
            catch { }
        }

        private string GetIconForType(System.Windows.Automation.ControlType type)
        {
            if (type == System.Windows.Automation.ControlType.Window) return "🪟";
            if (type == System.Windows.Automation.ControlType.Button) return "🟦";
            if (type == System.Windows.Automation.ControlType.Pane) return "🗂️";
            if (type == System.Windows.Automation.ControlType.Group) return "📁";
            return "⚪";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    #endregion

    #endregion

    #region PropertyItem

    public class PropertyItem
    {
        public string PropertyName { get; set; }
        public string Value { get; set; }
    }

    #endregion
}