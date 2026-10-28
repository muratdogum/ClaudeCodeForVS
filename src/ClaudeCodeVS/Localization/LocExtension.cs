using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace ClaudeCodeVS.Localization
{
    /// <summary>
    /// XAML access to <see cref="L.T"/>: <c>Text="{loc:Loc 'Yeni sohbet'}"</c>. The value is a binding, so it follows
    /// language changes made in Tools > Options without reopening the window.
    /// </summary>
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension()
        {
        }

        public LocExtension(string key)
        {
            Key = key;
        }

        [ConstructorArgument("key")]
        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var binding = new Binding(nameof(LocSource.Revision))
            {
                Source = LocSource.Instance,
                Mode = BindingMode.OneWay,
                Converter = KeyConverter.Instance,
                ConverterParameter = Key,
            };
            return binding.ProvideValue(serviceProvider);
        }

        private sealed class KeyConverter : IValueConverter
        {
            public static readonly KeyConverter Instance = new KeyConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => L.T(parameter as string);

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
        }
    }

    /// <summary>Binding source whose <see cref="Revision"/> changes with the UI language.</summary>
    public sealed class LocSource : INotifyPropertyChanged
    {
        public static readonly LocSource Instance = new LocSource();

        private LocSource()
        {
            L.LanguageChanged += (s, e) =>
            {
                Revision++;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
            };
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public int Revision { get; private set; }
    }
}
