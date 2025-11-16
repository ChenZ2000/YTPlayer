using System.Collections.Generic;

namespace YTPlayer
{
    public partial class MainForm
    {
        private sealed class SortState<T>
        {
            private readonly Dictionary<T, string> _accessibleDescriptions;
            private readonly IEqualityComparer<T> _comparer;

            public SortState(T initialOption, IDictionary<T, string> accessibleDescriptions)
            {
                _comparer = EqualityComparer<T>.Default;
                CurrentOption = initialOption;
                _accessibleDescriptions = new Dictionary<T, string>(accessibleDescriptions, _comparer);
            }

            public T CurrentOption { get; private set; }

            public string AccessibleDescription
                => _accessibleDescriptions.TryGetValue(CurrentOption, out var desc) ? desc : string.Empty;

            public void SetOption(T option)
            {
                CurrentOption = option;
            }

            public bool EqualsOption(T option)
            {
                return _comparer.Equals(CurrentOption, option);
            }

            public string GetDescription(T option)
            {
                return _accessibleDescriptions.TryGetValue(option, out var desc) ? desc : string.Empty;
            }
        }
    }
}
