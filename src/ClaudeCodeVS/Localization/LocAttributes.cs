using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace ClaudeCodeVS.Localization
{
    // Property grid attributes for Tools > Options whose text is looked up through L.

    [AttributeUsage(AttributeTargets.Property)]
    internal sealed class LocalizedCategoryAttribute : CategoryAttribute
    {
        public LocalizedCategoryAttribute(string category) : base(category)
        {
        }

        protected override string GetLocalizedString(string value) => L.T(value);
    }

    [AttributeUsage(AttributeTargets.Property)]
    internal sealed class LocalizedDisplayNameAttribute : DisplayNameAttribute
    {
        public LocalizedDisplayNameAttribute(string displayName) : base(displayName)
        {
        }

        public override string DisplayName => L.T(DisplayNameValue);
    }

    [AttributeUsage(AttributeTargets.Property)]
    internal sealed class LocalizedDescriptionAttribute : DescriptionAttribute
    {
        public LocalizedDescriptionAttribute(string description) : base(description)
        {
        }

        public override string Description => L.T(DescriptionValue);
    }

    /// <summary>Interface language choice; the values after <see cref="Auto"/> follow <see cref="L.Languages"/>.</summary>
    [TypeConverter(typeof(UiLanguageConverter))]
    public enum UiLanguage
    {
        Auto,
        Turkish,
        English,
        German,
        French,
        Spanish,
        Italian,
        PortugueseBrazil,
        Russian,
        Polish,
        Czech,
        Japanese,
        Korean,
        ChineseSimplified,
        ChineseTraditional,
        Dutch,
        Ukrainian,
    }

    internal static class UiLanguageExtensions
    {
        /// <summary>Language code for <see cref="L.Initialize"/>, or null for Visual Studio's language.</summary>
        public static string Code(this UiLanguage language) =>
            language == UiLanguage.Auto || (int)language > L.Languages.Length ? null : L.Languages[(int)language - 1].Code;

        public static string DisplayName(this UiLanguage language) =>
            language == UiLanguage.Auto || (int)language > L.Languages.Length
                ? L.T("Otomatik (Visual Studio dili)")
                : L.Languages[(int)language - 1].Name;
    }

    /// <summary>Shows native language names in the property grid; settings storage (no context) keeps the enum names.</summary>
    internal sealed class UiLanguageConverter : EnumConverter
    {
        public UiLanguageConverter() : base(typeof(UiLanguage))
        {
        }

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        {
            return context != null && destinationType == typeof(string) && value is UiLanguage language
                ? language.DisplayName()
                : base.ConvertTo(context, culture, value, destinationType);
        }

        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
        {
            if (value is string text)
            {
                var match = Enum.GetValues(typeof(UiLanguage)).Cast<UiLanguage>().FirstOrDefault(l => l.DisplayName() == text);
                if (match != UiLanguage.Auto || text == UiLanguage.Auto.DisplayName())
                {
                    return match;
                }
            }

            return base.ConvertFrom(context, culture, value);
        }
    }
}
