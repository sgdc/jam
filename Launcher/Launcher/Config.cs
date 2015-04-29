using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using NLog;

namespace Launcher
{
    public struct ConfigParseOption
    {
        public string Name;
        public Func<string, StreamReader, Logger, object> Parser;
    }

    public class Config
    {
        private Dictionary<string, object> configValues = new Dictionary<string, object>();

        private Dictionary<string, ConfigParseOption> parseOptions = new Dictionary<string, ConfigParseOption>();

        public Config(params ConfigParseOption[] options)
            : this(string.Empty, options)
        {
        }

        public Config(string path, params ConfigParseOption[] options)
        {
            foreach(var kv in options
                    .Where(opt => !string.IsNullOrWhiteSpace(opt.Name) && (opt.Parser != null))
                    .Select(opt =>
                {
                    //XXX Isn't there an easier way to add to a dictionary? I thought there was a way to add many key-value pairs at once... I also thought there was a simple iteration function that wasn't a for loop.
                    return new KeyValuePair<string, ConfigParseOption>("[" + opt.Name.ToLower() + "]", opt);
                }))
            {
                ((IDictionary<string, ConfigParseOption>)parseOptions).Add(kv);
            }
            Load(path);
        }

        public bool Load(string path, Logger log = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }
            if (log == null)
            {
                log = LogManager.CreateNullLogger();
            }
            configValues.Clear();

            using (var sr = new StreamReader(path))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("%"))
                    {
                        ConfigParseOption option;
                        if (parseOptions.TryGetValue(line.ToLower(), out option))
                        {
                            var output = option.Parser(sr.ReadLine(), sr, log);
                            if (output != null)
                            {
                                configValues.Add(option.Name, output);
                            }
                        }
                        else if (line.StartsWith("[") && line.EndsWith("]"))
                        {
                            log.Info("Unknown Info key: \"{0}\"", line);
                        }
                    }
                }
            }
            return true;
        }

        public bool ContainsKey(string key)
        {
            return configValues.ContainsKey(key);
        }

        public T GetValue<T>(string key)
        {
            if (configValues.ContainsKey(key))
            {
                object value = configValues[key];
                if(value is T)
                {
                    return (T)value;
                }
            }
            return default(T);
        }

        public object GetValueRaw(string key)
        {
            if (configValues.ContainsKey(key))
            {
                return configValues[key];
            }
            return null;
        }
    }
}
