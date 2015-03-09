﻿using System.Configuration;

namespace Any.Proxy.Configuration
{
    public class RemoteSection : ConfigurationSection
    {
        [ConfigurationProperty("password", IsRequired = true)]
        public string Password
        {
            get { return (string)this["password"]; }
        }

        [ConfigurationProperty("prefixes", IsRequired = true)]
        public string Prefixes
        {
            get { return (string)this["prefixes"]; }
        }
    }
}