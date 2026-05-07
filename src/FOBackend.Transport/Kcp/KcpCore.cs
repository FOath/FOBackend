using System.Buffers.Binary;

namespace FOBackend.Transport.Kcp;

/// <summary>
/// C# 实现的 KCP 协议核心，兼容 skywind3000/kcp 协议格式
/// 针对 60 FPS 实时对战游戏优化（低延迟模式）
/// </summary>
internal sealed class KcpCore
{
    // ==================== 协议常量 ====================

    public const int IKCP_OVERHEAD = 24;
    private const int IKCP_CMD_PUSH = 81;
    private const int IKCP_CMD_ACK = 82;
    private const int IKCP_CMD_WASK = 83;
    private const int IKCP_CMD_WINS = 84;

    private const int IKCP_ASK_SEND = 1;
    private const int IKCP_ASK_TELL = 2;

    private const uint IKCP_MTU_DEF = 1400;
    private const uint IKCP_INTERVAL = 100;
    private const uint IKCP_RTO_DEF = 200;
    private const uint IKCP_RTO_MIN = 100;
    private const uint IKCP_RTO_NDL = 30;
    private const uint IKCP_RTO_MAX = 60000;
    private const int IKCP_THRESH_INIT = 2;
    private const int IKCP_THRESH_MIN = 2;
    private const int IKCP_PROBE_INIT = 7000;
    private const int IKCP_PROBE_LIMIT = 120000;
    private const int IKCP_DEADLINK = 20;
    private const uint IKCP_WND_SND = 32;
    private const uint IKCP_WND_RCV = 128;

    // ==================== 字段 ====================

    public uint Conv { get; }
    public uint Mtu { get; private set; } = IKCP_MTU_DEF;
    public int WaitSnd => _sndBuf.Count + _sndQueue.Count;

    private int _mss;
    private byte _state;
    private uint _sndUna;
    private uint _sndNxt;
    private uint _rcvNxt;
    private uint _ssthresh = IKCP_THRESH_INIT;
    private int _rxRttVal;
    private int _rxSrtt;
    private int _rxRto = (int)IKCP_RTO_DEF;
    private uint _rxMinRto = IKCP_RTO_MIN;
    private uint _sndWnd = IKCP_WND_SND;
    private uint _rcvWnd = IKCP_WND_RCV;
    private uint _rmtWnd = IKCP_WND_RCV;
    private uint _cwnd;
    private uint _incr;
    private uint _probe;
    private uint _current;
    private uint _interval = IKCP_INTERVAL;
    private uint _tsFlush;
    private uint _tsProbe;
    private uint _probeWait;
    private bool _updated;
    private bool _noDelay;
    private int _fastResend;
    private bool _noCongestionWindow;
    private bool _stream;
    private int _deadLink = IKCP_DEADLINK;

    private readonly List<Segment> _sndQueue = new();
    private readonly List<Segment> _rcvQueue = new();
    private readonly List<Segment> _sndBuf = new();
    private readonly List<Segment> _rcvBuf = new();
    private readonly List<(uint Sn, uint Ts)> _ackList = new();

    private readonly Action<byte[]> _output;
    private byte[] _buffer;
    private readonly object _lock = new();

    // ==================== 构造函数 ====================

    public KcpCore(uint conv, Action<byte[]> output)
    {
        Conv = conv;
        _output = output;
        _mss = (int)(Mtu - IKCP_OVERHEAD);
        _buffer = new byte[(Mtu + IKCP_OVERHEAD) * 3];
    }

    // ==================== 公共配置方法 ====================

    public int Interval(int interval)
    {
        if (interval > 5000) interval = 5000;
        else if (interval < 10) interval = 10;
        _interval = (uint)interval;
        return 0;
    }

    public int SetMtu(int mtu)
    {
        if (mtu < 50 || mtu < IKCP_OVERHEAD) return -1;
        Mtu = (uint)mtu;
        _mss = (int)(Mtu - IKCP_OVERHEAD);
        _buffer = new byte[(mtu + IKCP_OVERHEAD) * 3];
        return 0;
    }

    public int WndSize(int sndwnd, int rcvwnd)
    {
        if (sndwnd > 0) _sndWnd = (uint)sndwnd;
        if (rcvwnd > 0) _rcvWnd = (uint)rcvwnd;
        return 0;
    }

    public int NoDelay(int nodelay, int interval, int resend, int nc)
    {
        if (nodelay >= 0)
        {
            _noDelay = nodelay != 0;
            _rxMinRto = _noDelay ? IKCP_RTO_NDL : IKCP_RTO_MIN;
        }
        if (interval >= 0)
        {
            if (interval > 5000) interval = 5000;
            else if (interval < 10) interval = 10;
            _interval = (uint)interval;
        }
        if (resend >= 0) _fastResend = resend;
        if (nc >= 0) _noCongestionWindow = nc != 0;
        return 0;
    }

    public void SetMinRto(int minrto)
    {
        _rxMinRto = (uint)minrto;
    }

    // ==================== 发送 / 接收 ====================

    public int Send(ReadOnlySpan<byte> buffer)
    {
        lock (_lock)
        {
            int offset = 0;
            if (buffer.Length == 0) return -1;

            // 流模式：追加到最后一个 segment
            if (_stream)
            {
                if (_sndQueue.Count > 0)
                {
                    var old = _sndQueue[^1];
                    int capacity = _mss - old.Data.Length;
                    if (capacity > 0)
                    {
                        int extend = buffer.Length < capacity ? buffer.Length : capacity;
                        var newData = new byte[old.Data.Length + extend];
                        old.Data.CopyTo(newData, 0);
                        buffer.Slice(0, extend).CopyTo(newData.AsSpan(old.Data.Length));
                        old.Data = newData;
                        offset += extend;
                    }
                }
                if (offset >= buffer.Length) return 0;

                while (offset < buffer.Length)
                {
                    int size = buffer.Length - offset;
                    if (size > _mss) size = _mss;
                    _sndQueue.Add(new Segment { Data = buffer.Slice(offset, size).ToArray() });
                    offset += size;
                }
                return 0;
            }

            // 消息模式：计算分片数
            int count;
            if (buffer.Length <= _mss) count = 1;
            else count = (buffer.Length + _mss - 1) / _mss;

            if (count >= 255) return -2;
            if (count == 0) count = 1;

            for (int i = 0; i < count; i++)
            {
                int size = buffer.Length - offset;
                if (size > _mss) size = _mss;
                _sndQueue.Add(new Segment
                {
                    Data = buffer.Slice(offset, size).ToArray(),
                    Frg = (byte)(count - i - 1)
                });
                offset += size;
            }

            return 0;
        }
    }

    public int PeekSize()
    {
        lock (_lock)
        {
            if (_rcvQueue.Count == 0) return -1;
            var seg = _rcvQueue[0];
            if (seg.Frg == 0) return seg.Data.Length;
            if (_rcvQueue.Count < seg.Frg + 1) return -1;

            int length = 0;
            for (int i = 0; i <= seg.Frg; i++)
                length += _rcvQueue[i].Data.Length;
            return length;
        }
    }

    public int Recv(Span<byte> buffer)
    {
        lock (_lock)
        {
            if (_rcvQueue.Count == 0) return -1;

            int peekSize = PeekSize();
            if (peekSize < 0) return -2;
            if (peekSize > buffer.Length) return -3;

            bool fastRecover = _rcvQueue.Count >= _rcvWnd;
            int offset = 0;
            int count = 0;

            for (int i = 0; i < _rcvQueue.Count; i++)
            {
                var seg = _rcvQueue[i];
                seg.Data.CopyTo(buffer.Slice(offset));
                offset += seg.Data.Length;
                count++;
                if (seg.Frg == 0) break;
            }

            if (count > 0) _rcvQueue.RemoveRange(0, count);

            // 将 rcv_buf 中连续的 segment 移到 rcv_queue
            while (_rcvBuf.Count > 0)
            {
                var seg = _rcvBuf[0];
                if (seg.Sn == _rcvNxt && _rcvQueue.Count < _rcvWnd)
                {
                    _rcvBuf.RemoveAt(0);
                    _rcvQueue.Add(seg);
                    _rcvNxt++;
                }
                else break;
            }

            if (_rcvQueue.Count < _rcvWnd && fastRecover)
                _probe |= IKCP_ASK_TELL;

            return offset;
        }
    }

    // ==================== 输入 / 处理 ====================

    public int Input(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            uint prevUna = _sndUna;
            uint maxack = 0;
            int flag = 0;

            if (data.Length < IKCP_OVERHEAD) return -1;

            while (true)
            {
                if (data.Length < IKCP_OVERHEAD) break;

                uint conv = BinaryPrimitives.ReadUInt32LittleEndian(data);
                if (conv != Conv) return -1;

                byte cmd = data[4];
                byte frg = data[5];
                ushort wnd = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
                uint ts = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8));
                uint sn = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12));
                uint una = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16));
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20));

                data = data.Slice(IKCP_OVERHEAD);
                if (data.Length < len) return -2;

                if (cmd != IKCP_CMD_PUSH && cmd != IKCP_CMD_ACK &&
                    cmd != IKCP_CMD_WASK && cmd != IKCP_CMD_WINS)
                    return -3;

                _rmtWnd = wnd;
                ParseUna(una);
                ShrinkBuf();

                if (cmd == IKCP_CMD_ACK)
                {
                    if (_itimediff(_current, ts) >= 0)
                        UpdateAck(_itimediff(_current, ts));
                    ParseAck(sn);
                    ShrinkBuf();
                    if (flag == 0)
                    {
                        flag = 1;
                        maxack = sn;
                    }
                    else if (_itimediff(sn, maxack) > 0)
                    {
                        maxack = sn;
                    }
                }
                else if (cmd == IKCP_CMD_PUSH)
                {
                    if (_itimediff(sn, _rcvNxt + _rcvWnd) < 0)
                    {
                        _ackList.Add((sn, ts));
                        if (_itimediff(sn, _rcvNxt) >= 0)
                        {
                            var seg = new Segment
                            {
                                Conv = conv,
                                Cmd = cmd,
                                Frg = frg,
                                Wnd = wnd,
                                Ts = ts,
                                Sn = sn,
                                Una = una,
                                Data = data.Slice(0, (int)len).ToArray()
                            };
                            // 避免重复并排序
                            bool duplicate = false;
                            for (int i = 0; i < _rcvBuf.Count; i++)
                            {
                                if (_rcvBuf[i].Sn == sn) { duplicate = true; break; }
                            }
                            if (!duplicate)
                            {
                                _rcvBuf.Add(seg);
                                _rcvBuf.Sort((a, b) => _itimediff(a.Sn, b.Sn) > 0 ? 1 : -1);
                            }
                        }
                    }
                }
                else if (cmd == IKCP_CMD_WASK)
                {
                    _probe |= IKCP_ASK_TELL;
                }
                else if (cmd == IKCP_CMD_WINS)
                {
                    // nothing
                }
                else
                {
                    return -3;
                }

                data = data.Slice((int)len);
            }

            if (flag != 0) ParseFastack(maxack);

            // 拥塞控制
            if (!_noCongestionWindow)
            {
                if (_itimediff(_sndUna, prevUna) > 0)
                {
                    if (_cwnd < _ssthresh)
                    {
                        _cwnd++;
                        _incr += (uint)_mss;
                    }
                    else
                    {
                        if (_incr < (uint)_mss) _incr = (uint)_mss;
                        _incr += (uint)(_mss * _mss) / _incr + (uint)(_mss / 16);
                        if ((_cwnd + 1) * (uint)_mss <= _incr)
                            _cwnd = (_incr + (uint)_mss - 1) / (uint)_mss;
                    }
                    if (_cwnd > _sndWnd)
                    {
                        _cwnd = _sndWnd;
                        _incr = _sndWnd * (uint)_mss;
                    }
                }
            }

            // 将 rcv_buf 中连续的 segment 移到 rcv_queue
            while (_rcvBuf.Count > 0)
            {
                var seg = _rcvBuf[0];
                if (seg.Sn == _rcvNxt && _rcvQueue.Count < _rcvWnd)
                {
                    _rcvBuf.RemoveAt(0);
                    _rcvQueue.Add(seg);
                    _rcvNxt++;
                }
                else break;
            }

            return 0;
        }
    }

    // ==================== 更新 / Flush ====================

    public void Update(uint current)
    {
        lock (_lock)
        {
            _current = current;
            if (!_updated)
            {
                _updated = true;
                _tsFlush = _current;
            }

            int slap = _itimediff(_current, _tsFlush);
            if (slap >= 10000 || slap < -10000)
            {
                _tsFlush = _current;
                slap = 0;
            }

            if (slap >= 0)
            {
                _tsFlush += _interval;
                if (_itimediff(_current, _tsFlush) >= 0)
                    _tsFlush = _current + _interval;
                Flush();
            }
        }
    }

    public uint Check(uint current)
    {
        lock (_lock)
        {
            uint tsFlush = _tsFlush;
            if (!_updated) return current;

            int slap = _itimediff(current, tsFlush);
            if (slap >= 10000 || slap < -10000)
                tsFlush = current;

            if (slap >= 0) return current;

            uint tmFlush = (uint)_itimediff(tsFlush, current);
            uint tmPacket = 0xFFFFFFFF;

            for (int i = 0; i < _sndBuf.Count; i++)
            {
                int diff = _itimediff(_sndBuf[i].ResendTs, current);
                if (diff <= 0) return current;
                if ((uint)diff < tmPacket) tmPacket = (uint)diff;
            }

            uint minimal = tmFlush < tmPacket ? tmFlush : tmPacket;
            if (minimal >= _interval) minimal = _interval;

            return current + minimal;
        }
    }

    private void Flush()
    {
        uint current = _current;
        var buffer = _buffer;
        int size = 0;
        bool lostSeg = false;
        bool change = false;

        // 1. Flush ACKs
        for (int i = 0; i < _ackList.Count; i++)
        {
            if (size + IKCP_OVERHEAD > Mtu)
            {
                _output(buffer.AsSpan(0, size).ToArray());
                size = 0;
            }
            var (sn, ts) = _ackList[i];
            size += Segment.Encode(buffer.AsSpan(size), new Segment
            {
                Conv = Conv,
                Cmd = IKCP_CMD_ACK,
                Sn = sn,
                Ts = ts,
                Wnd = (ushort)WndUnused()
            });
        }
        _ackList.Clear();

        // 2. Probe window size
        if (_rmtWnd == 0)
        {
            if (_probeWait == 0)
            {
                _probeWait = IKCP_PROBE_INIT;
                _tsProbe = current + _probeWait;
            }
            else if (_itimediff(current, _tsProbe) >= 0)
            {
                if (_probeWait < IKCP_PROBE_INIT) _probeWait = IKCP_PROBE_INIT;
                _probeWait += _probeWait / 2;
                if (_probeWait > IKCP_PROBE_LIMIT) _probeWait = IKCP_PROBE_LIMIT;
                _tsProbe = current + _probeWait;
                _probe |= IKCP_ASK_SEND;
            }
        }
        else
        {
            _tsProbe = 0;
            _probeWait = 0;
        }

        // 3. Flush window probe
        if ((_probe & IKCP_ASK_SEND) != 0)
        {
            if (size + IKCP_OVERHEAD > Mtu)
            {
                _output(buffer.AsSpan(0, size).ToArray());
                size = 0;
            }
            size += Segment.Encode(buffer.AsSpan(size), new Segment
            {
                Conv = Conv,
                Cmd = IKCP_CMD_WASK,
                Wnd = (ushort)WndUnused()
            });
        }

        if ((_probe & IKCP_ASK_TELL) != 0)
        {
            if (size + IKCP_OVERHEAD > Mtu)
            {
                _output(buffer.AsSpan(0, size).ToArray());
                size = 0;
            }
            size += Segment.Encode(buffer.AsSpan(size), new Segment
            {
                Conv = Conv,
                Cmd = IKCP_CMD_WINS,
                Wnd = (ushort)WndUnused()
            });
        }
        _probe = 0;

        // 4. Calculate congestion window
        uint cwnd = Math.Min(_sndWnd, _rmtWnd);
        if (!_noCongestionWindow) cwnd = Math.Min(_cwnd, cwnd);

        // 5. Move data from snd_queue to snd_buf
        while (_sndQueue.Count > 0)
        {
            if (_itimediff(_sndNxt, _sndUna + cwnd) >= 0) break;
            var newSeg = _sndQueue[0];
            _sndQueue.RemoveAt(0);
            newSeg.Conv = Conv;
            newSeg.Cmd = IKCP_CMD_PUSH;
            newSeg.Wnd = (ushort)WndUnused();
            newSeg.Ts = current;
            newSeg.Sn = _sndNxt++;
            newSeg.Una = _rcvNxt;
            newSeg.ResendTs = current;
            newSeg.Rto = (uint)_rxRto;
            newSeg.FastAck = 0;
            newSeg.Xmit = 0;
            _sndBuf.Add(newSeg);
        }

        // 6. Flush snd_buf
        uint resent = _fastResend > 0 ? (uint)_fastResend : 0xFFFFFFFF;
        uint rtomin = _noDelay ? 0 : (uint)(_rxRto >> 3);

        for (int i = 0; i < _sndBuf.Count; i++)
        {
            var segment = _sndBuf[i];
            bool needsend = false;

            if (segment.Xmit == 0)
            {
                needsend = true;
                segment.Xmit++;
                segment.Rto = (uint)_rxRto;
                segment.ResendTs = current + segment.Rto + rtomin;
            }
            else if (_itimediff(current, segment.ResendTs) >= 0)
            {
                needsend = true;
                segment.Xmit++;
                if (_noDelay)
                    segment.Rto += (uint)(_rxRto / 2);
                else
                    segment.Rto += Math.Max(segment.Rto, (uint)_rxRto);
                segment.ResendTs = current + segment.Rto;
                lostSeg = true;
            }
            else if (segment.FastAck >= resent)
            {
                needsend = true;
                segment.Xmit++;
                segment.FastAck = 0;
                segment.ResendTs = current + segment.Rto;
                change = true;
            }

            if (needsend)
            {
                segment.Ts = current;
                segment.Wnd = (ushort)WndUnused();
                segment.Una = _rcvNxt;

                int need = IKCP_OVERHEAD + segment.Data.Length;
                if (size + need > Mtu)
                {
                    _output(buffer.AsSpan(0, size).ToArray());
                    size = 0;
                }
                size += Segment.Encode(buffer.AsSpan(size), segment);

                if (segment.Xmit >= _deadLink)
                    _state = 1;
            }
        }

        // 7. Flush remaining
        if (size > 0)
        {
            _output(buffer.AsSpan(0, size).ToArray());
        }
    }

    // ==================== 内部辅助方法 ====================

    private static int _itimediff(uint later, uint earlier)
    {
        return (int)((uint)(later - earlier));
    }

    private int WndUnused()
    {
        if (_rcvQueue.Count < _rcvWnd)
            return (int)(_rcvWnd - _rcvQueue.Count);
        return 0;
    }

    private void UpdateAck(int rtt)
    {
        if (_rxSrtt == 0)
        {
            _rxSrtt = rtt;
            _rxRttVal = rtt / 2;
        }
        else
        {
            int delta = rtt - _rxSrtt;
            if (delta < 0) delta = -delta;
            _rxRttVal = (3 * _rxRttVal + delta) / 4;
            _rxSrtt = (7 * _rxSrtt + rtt) / 8;
            if (_rxSrtt < 1) _rxSrtt = 1;
        }
        int rto = _rxSrtt + Math.Max((int)_interval, 4 * _rxRttVal);
        _rxRto = (int)Math.Max(_rxMinRto, Math.Min((uint)rto, IKCP_RTO_MAX));
    }

    private void ShrinkBuf()
    {
        if (_sndBuf.Count > 0)
            _sndUna = _sndBuf[0].Sn;
        else
            _sndUna = _sndNxt;
    }

    private void ParseAck(uint sn)
    {
        if (_itimediff(sn, _sndUna) < 0 || _itimediff(sn, _sndNxt) >= 0) return;

        for (int i = 0; i < _sndBuf.Count; i++)
        {
            if (_sndBuf[i].Sn == sn)
            {
                _sndBuf.RemoveAt(i);
                break;
            }
            if (_itimediff(sn, _sndBuf[i].Sn) < 0) break;
        }
    }

    private void ParseUna(uint una)
    {
        for (int i = 0; i < _sndBuf.Count; )
        {
            if (_itimediff(una, _sndBuf[i].Sn) > 0)
                _sndBuf.RemoveAt(i);
            else
                break;
        }
    }

    private void ParseFastack(uint sn)
    {
        if (_itimediff(sn, _sndUna) < 0 || _itimediff(sn, _sndNxt) >= 0) return;

        for (int i = 0; i < _sndBuf.Count; i++)
        {
            if (_itimediff(sn, _sndBuf[i].Sn) < 0) break;
            if (sn != _sndBuf[i].Sn)
                _sndBuf[i].FastAck++;
        }
    }

    // ==================== Segment ====================

    private sealed class Segment
    {
        public uint Conv;
        public byte Cmd;
        public byte Frg;
        public ushort Wnd;
        public uint Ts;
        public uint Sn;
        public uint Una;
        public byte[] Data = Array.Empty<byte>();

        public uint ResendTs;
        public uint Rto;
        public uint FastAck;
        public uint Xmit;

        public static int Encode(Span<byte> dst, Segment seg)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(0, 4), seg.Conv);
            dst[4] = seg.Cmd;
            dst[5] = seg.Frg;
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(6, 2), seg.Wnd);
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(8, 4), seg.Ts);
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(12, 4), seg.Sn);
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(16, 4), seg.Una);
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(20, 4), (uint)seg.Data.Length);
            if (seg.Data.Length > 0)
                seg.Data.CopyTo(dst.Slice(24));
            return IKCP_OVERHEAD + seg.Data.Length;
        }
    }
}
