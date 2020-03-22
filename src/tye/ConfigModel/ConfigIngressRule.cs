using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Tye.ConfigModel
{
    public class ConfigIngressRule
    {
        public string? Path { get; set; }
        public string? Host { get; set; }
        public string? Service { get; set; }
    }
}
