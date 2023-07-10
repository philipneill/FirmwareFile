/**
 * @file
 * @copyright  Copyright (c) 2020 Jesús González del Río
 * @license    See LICENSE.txt
 */

using System;
using System.IO;
using System.Threading.Tasks;

namespace FirmwareFile
{
    /**
     * Loader for firmware files in Intel (HEX) format.
     */
    public static class IntelFileLoader
    {
        /*===========================================================================
         *                            PUBLIC METHODS
         *===========================================================================*/

        /**
         * Loads a firmware from the file at the given path.
         * 
         * @param [in] filePath Path to the file containing the firmware
         */
        public static Firmware Load( string filePath, bool noBlockMerging = false)
        {
            return LoadAsync( filePath, noBlockMerging ).GetAwaiter().GetResult();
        }

        /**
         * Loads a firmware from the file at the given stream.
         * 
         * @param [in] stream Stream to provide the firmware file contents
         */
        public static Firmware Load( Stream stream, bool noBlockMerging = false)
        {
            return LoadAsync( stream, noBlockMerging ).GetAwaiter().GetResult();
        }

        /**
         * Loads asynchronously a firmware from the file at the given path.
         * 
         * @param [in] filePath Path to the file containing the firmware
         */
        public static async Task<Firmware> LoadAsync( string filePath, bool noBlockMerging = false)
        {
            using var fileStream = new FileStream( filePath, FileMode.Open, FileAccess.Read );
            return await LoadAsync( fileStream, noBlockMerging );
        }

        /**
         * Loads asynchronously a firmware from the file at the given stream.
         * 
         * @param [in] stream Stream to provide the firmware file contents
         */
        public static async Task<Firmware> LoadAsync( Stream stream, bool noBlockMerging = false)
        {
            var fwFile = new Firmware( true );

            var fileReader = new StreamReader( stream );

            int lineNumber = 0;
            UInt32 extendedLinearAddress = 0;
            UInt32 extendedSegmentAddress = 0;
            bool eofExpected = false;

            while( !fileReader.EndOfStream )
            {
                string line = ( await fileReader.ReadLineAsync() )?.TrimEnd() ?? "";

                lineNumber++;

                if( eofExpected )
                {
                    throw new FormatException( "Record found after EOF record", lineNumber );
                }

                if( line.Length > 0 )
                {
                    try
                    {
                        var record = ProcessLine( line );

                        record.Address += extendedLinearAddress;
                        record.Address += extendedSegmentAddress;

                        switch( record.Type )
                        {
                            case RecordType.DATA:
                                ProcessDataRecord( record, fwFile, noBlockMerging );
                                break;

                            case RecordType.EOF:
                                eofExpected = true;
                                break;

                            case RecordType.EXTENDED_LINEAR_ADDRESS:
                                extendedLinearAddress = GetExtendedLinearAddress(record);
                                break;

                            case RecordType.EXTENDED_SEGMENT_ADDRESS:
                                extendedSegmentAddress = GetExtendedSegmentAddress(record);
                                break;

                            default:
                                // Ignore other supported record types
                                break;
                        }
                    }
                    catch( Exception e )
                    {
                        throw new FormatException( e.Message, lineNumber );
                    }
                }
            }

            return fwFile;
        }

        /*===========================================================================
         *                         PRIVATE NESTED CLASSES
         *===========================================================================*/

        private enum RecordType
        {
            DATA,
            EOF,
            EXTENDED_SEGMENT_ADDRESS,
            START_SEGMENT_ADDRESS,
            EXTENDED_LINEAR_ADDRESS,
            START_LINEAR_ADDRESS
        }

        private class Record
        {
            public RecordType Type;
            public UInt32 Address;
            public byte[] Data;

            public Record( RecordType type, UInt32 address, byte[] data )
            {
                Type = type;
                Address = address;
                Data = data;
            }
        }

        /*===========================================================================
         *                            PRIVATE METHODS
         *===========================================================================*/

        private static Record ProcessLine( string line )
        {
            if( line.Length < ( DATA_INDEX + CHECKSUM_SIZE ) )
            {
                throw new Exception( "Truncated record" );
            }

            if( line[START_CODE_INDEX] != START_CODE )
            {
                throw new Exception( $"Invalid start code '{line[0]}' ({(int)line[0]:X2}h)" );
            }

            int byteCount;
            UInt32 address;
            int recordTypeCode;

            try
            {
                byteCount = Convert.ToInt32( line.Substring( BYTE_COUNT_INDEX, BYTE_COUNT_SIZE ), 16 );
                address = Convert.ToUInt32( line.Substring( ADDRESS_INDEX, ADDRESS_SIZE ), 16 );
                recordTypeCode = Convert.ToInt32( line.Substring( RECORD_TYPE_INDEX, RECORD_TYPE_SIZE ), 16 );
            }
            catch( Exception e )
            {
                throw new Exception( "Invalid hexadecimal value", e );
            }

            if( line.Length != ( DATA_INDEX + CHECKSUM_SIZE + ( byteCount * 2 ) ) )
            {
                throw new Exception( "Invalid record length" );
            }

            RecordType recordType = ConvertToRecordType( recordTypeCode );

            byte calculatedChecksum = (byte) byteCount;
            calculatedChecksum += (byte) ( ( address >> 0 ) & 0xFF );
            calculatedChecksum += (byte) ( ( address >> 8 ) & 0xFF );
            calculatedChecksum += (byte) recordTypeCode;

            byte[] data = new byte[ byteCount ];

            for( int i = 0; i < byteCount; i++ )
            {
                try
                {
                    data[i] = Convert.ToByte( line.Substring( DATA_INDEX + ( i * 2 ), 2 ), 16 );
                    calculatedChecksum += data[i];
                }
                catch( Exception e )
                {
                    throw new Exception( "Invalid hexadecimal value", e );
                }
            }

            calculatedChecksum = (byte) - (int) calculatedChecksum;

            byte checksum;

            try
            {
                checksum = Convert.ToByte( line.Substring( DATA_INDEX + ( byteCount * 2 ), CHECKSUM_SIZE ), 16 );
            }
            catch( Exception e )
            {
                throw new Exception( "Invalid hexadecimal value", e );
            }

            if( checksum != calculatedChecksum )
            {
                throw new Exception( $"Invalid checksum (expected: {calculatedChecksum:X2}h, reported: {checksum:X2}h)" );
            }

            return new Record( recordType, address, data );
        }

        private static RecordType ConvertToRecordType( int recordTypeCode )
        {
            return recordTypeCode switch
            {
                RECORD_TYPE_DATA_CODE => RecordType.DATA,
                RECORD_TYPE_EOF_CODE => RecordType.EOF,
                RECORD_TYPE_ELA_CODE => RecordType.EXTENDED_LINEAR_ADDRESS,
                RECORD_TYPE_ESA_CODE => RecordType.EXTENDED_SEGMENT_ADDRESS,
                RECORD_TYPE_SSA_CODE => RecordType.START_SEGMENT_ADDRESS,
                RECORD_TYPE_SLA_CODE => RecordType.START_LINEAR_ADDRESS,
                _ => throw new Exception($"Unsupported record type '{recordTypeCode:X2}h'"),
            };
        }

        private static UInt32 GetExtendedLinearAddress(Record record)
        {
            if (record.Data.Length != 2)
            {
                throw new Exception("Invalid data length for 'Extended Linear Address' record");
            }

            return (((UInt32)record.Data[0]) << 24) + (((UInt32)record.Data[1]) << 16);
        }
        private static UInt32 GetExtendedSegmentAddress(Record record)
        {
            if (record.Data.Length != 2)
            {
                throw new Exception("Invalid data length for 'Extended Segment Address' record");
            }

            return (((UInt32)record.Data[0]) << 12) + (((UInt32)record.Data[1]) << 4);
        }

        private static void ProcessDataRecord( Record record, Firmware fwFile, bool noBlockMerging = false )
        {
            fwFile.SetData( record.Address, record.Data, noBlockMerging );
        }

        /*===========================================================================
         *                           PRIVATE CONSTANTS
         *===========================================================================*/

        private const char START_CODE = ':';

        private const int RECORD_TYPE_DATA_CODE = 0x00;
        private const int RECORD_TYPE_EOF_CODE = 0x01;
        private const int RECORD_TYPE_ESA_CODE = 0x02;
        private const int RECORD_TYPE_SSA_CODE = 0x03;
        private const int RECORD_TYPE_ELA_CODE = 0x04;
        private const int RECORD_TYPE_SLA_CODE = 0x05;

        private const int START_CODE_INDEX = 0;
        private const int BYTE_COUNT_INDEX = 1;
        private const int ADDRESS_INDEX = 3;
        private const int RECORD_TYPE_INDEX = 7;
        private const int DATA_INDEX = 9;

        private const int BYTE_COUNT_SIZE = 2;
        private const int ADDRESS_SIZE = 4;
        private const int RECORD_TYPE_SIZE = 2;
        private const int CHECKSUM_SIZE = 2;
    }
}
