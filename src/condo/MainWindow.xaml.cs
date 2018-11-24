namespace condo
{
    using System.ComponentModel;
    using System.Windows;
    using ConsoleBuffer;

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private ConsoleWrapper console;
        private KeyHandler keyHandler;

        public MainWindow()
        {
            this.InitializeComponent();

            this.Loaded += this.OnLoaded;
#if DEBUG
            System.Diagnostics.Debugger.Launch();
#endif
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            this.console = TerminalManager.Instance.GetOrCreate(0, "wsl.exe");
            this.keyHandler = new KeyHandler(this.console);

            this.screen = new Screen(this.console.Buffer);
            this.scrollViewer.Content = this.screen;
            this.scrollViewer.CanContentScroll = true;

            this.console.Buffer.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == "Title")
                {
                    this.Dispatcher.InvokeAsync(() => this.Title = this.console.Buffer.Title);
                }
            };

            this.KeyDown += this.keyHandler.OnKeyDown;
            this.KeyDown += (_, args) => this.screen.VerticalOffset = double.MaxValue; // force scroll on keypress.
            this.TextInput += this.keyHandler.OnTextInput;

            this.console.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == "Running" && this.console != null && this.console.Running == false)
                {
                    this.KeyDown += (keySender, keyArgs) =>
                    {
                        if (keyArgs.Key == System.Windows.Input.Key.Enter)
                        {
                            this.Close();
                        }
                    };
                }
            };

            this.Closing += this.HandleClosing;
        }

        private void HandleClosing(object sender, CancelEventArgs e)
        {
            this.screen.Close();
            this.screen = null;

            this.console?.Dispose();
            this.console = null;
        }
    }
}
