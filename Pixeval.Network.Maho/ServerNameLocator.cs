namespace Pixeval.Network.Maho;

public enum ServerNameLocatingResult
{
    Located,
    InvalidRecordHeader,
    InvalidHandshakeHeader,
    InvalidServerNameExtensionNameType,
    ServerNameExtensionNotFound
}

// https://tls13.xargs.org/
public ref struct ServerNameLocator(ReadOnlySpan<byte> completePacket)
{
    private const int TlsRecordHeaderLength = 5;
    private const ushort ServerNameExtensionId = 0;
    private int _currentIndex = 0;
    private ReadOnlySpan<byte> _completePacket = completePacket;


    public ServerNameLocatingResult TryLocateServerName(out List<(int hostnameStart, int hostnameLength)> position)
    {
        position = [];
        if (!ReadAndValidateRecordHeader())
        {
            return ServerNameLocatingResult.InvalidRecordHeader;
        }

        if (!ReadAndValidateHandshakeHeader())
        {
            return ServerNameLocatingResult.InvalidHandshakeHeader;
        }

        ReadClientVersion();
        ReadClientRandom();
        ReadSessionId();
        ReadCipherSuites();
        ReadCompressionMethod();
        ReadExtensionLength();
        while (_currentIndex < _completePacket.Length)
        {
            var extensionReadResult = ReadExtension();
            var extensionContentStart = extensionReadResult.ExtensionContentStart;
            var extensionId = extensionReadResult.ExtensionId;
            var content = extensionReadResult.PureExtensionContent;
            if (extensionId == ServerNameExtensionId)
            {
                if (TryFindHostNameInServerNameExtension(content, out var hostNameLocations))
                {
                    position.AddRange(hostNameLocations.Select(loc => (loc.hostnameStart + extensionContentStart, loc.hostnameLength)));
                    continue;
                }

                position = [];
                return ServerNameLocatingResult.InvalidServerNameExtensionNameType;
            }
        }

        return position.Count == 0
            ? ServerNameLocatingResult.ServerNameExtensionNotFound
            : ServerNameLocatingResult.Located;
    }

    private static bool TryFindHostNameInServerNameExtension(ReadOnlySpan<byte> serverNameExtensionContent, out List<(int hostnameStart, int hostnameLength)> result)
    {
        var index = 0;
        if (serverNameExtensionContent.Length < 2)
        {
            result = [];
            return false;
        }
        index += 2; // Skip server name list length
        result = [];
        while (index < serverNameExtensionContent.Length)
        {
            if (serverNameExtensionContent.Length < index + 3)
            {
                result = [];
                return false; // Malformed extension content
            }
            var nameType = serverNameExtensionContent[index++];
            if (nameType != 0) // 0 for "DNS hostname"
            {
                result = [];
                return false;
            }
            var nameLength = serverNameExtensionContent[index++] << 8 | serverNameExtensionContent[index++];
            if (nameLength == 0)
            {
                continue; // Skip empty hostnames
            }
            if (serverNameExtensionContent.Length < index + nameLength)
            {
                result = [];
                return false; // Malformed extension content
            }
            result.Add((index, nameLength));
            index += nameLength;
        }

        return true;
    }

    private bool EnsurePacketComplete(int desiredLength)
    {
        return _completePacket.Length - TlsRecordHeaderLength == desiredLength;
    }

    private bool ReadAndValidateRecordHeader()
    {
        if (!IsTlsHandshakeRecord())
        {
            return false;
        }
        ReadProtocolVersion();
        var subsequentLength = ReadHandshakeLength();
        return EnsurePacketComplete(subsequentLength);
        
    }

    private bool IsTlsHandshakeRecord()
    {
        return _completePacket.Length >= TlsRecordHeaderLength && _completePacket[_currentIndex++] == 0x16;
    }

    private void ReadProtocolVersion()
    {
        _currentIndex += 2; // Skip major and minor version
    }

    private int ReadHandshakeLength()
    {
        return _completePacket[_currentIndex++] << 8 | _completePacket[_currentIndex++];
    }

    private bool ReadAndValidateHandshakeHeader()
    {
        const int handshakeHeaderLength = 4;
        if (_completePacket.Length - _currentIndex < handshakeHeaderLength)
        {
            return false;
        }
        
        if (_completePacket[_currentIndex++] != 0x01) // 0x01: ClientHello
        {
            return false;
        }

        _currentIndex += 3; // skip handshake length specifier
        return true;
    }

    private void ReadClientVersion()
    {
        _currentIndex += 2;
    }

    private void ReadClientRandom()
    {
        _currentIndex += 32;
    }

    private void ReadSessionId()
    {
        var sessionIdLength = _completePacket[_currentIndex++];
        _currentIndex += sessionIdLength;
    }

    private void ReadCipherSuites()
    {
        var cipherSuiteLength = _completePacket[_currentIndex++] << 8 | _completePacket[_currentIndex++];
        _currentIndex += cipherSuiteLength;
    }

    private void ReadCompressionMethod()
    {
        var compressionMethodLength = _completePacket[_currentIndex++];
        _currentIndex += compressionMethodLength;
    }

    private void ReadExtensionLength()
    {
        _currentIndex += 2;
    }

    private readonly ref struct ExtensionReadResult(int extensionContentStart, ushort extensionId, ReadOnlySpan<byte> pureExtensionContent)
    {
        public int ExtensionContentStart => extensionContentStart;

        public ushort ExtensionId => extensionId;

        public ReadOnlySpan<byte> PureExtensionContent { get; } = pureExtensionContent;
    }

    private ExtensionReadResult ReadExtension()
    {
        var eId = (ushort) (_completePacket[_currentIndex++] << 8 | _completePacket[_currentIndex++]);
        var length = _completePacket[_currentIndex++] << 8 | _completePacket[_currentIndex++];
        var extensionContentStart = _currentIndex;
        var content = _completePacket[_currentIndex..(_currentIndex + length)];
        _currentIndex += length;
        return new ExtensionReadResult(extensionContentStart, eId, content);
    }
}