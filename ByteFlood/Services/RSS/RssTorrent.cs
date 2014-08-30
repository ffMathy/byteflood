﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ByteFlood.Services.RSS
{
    public class RssTorrent
    {
        public string Name { get; set; }
        public string TorrentFileUrl { get; set; }
        public string Summary { get; set; }
        public string TorrentFilePath { get; set; }
        public bool Success { get; set; }
        public DateTime TimePublished { get; set; }
    }
}