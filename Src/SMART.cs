using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management;

// References / credits:
//    https://github.com/DoogeJ/SmartHDD/blob/master/SmartHDD/Program.cs
//    https://stackoverflow.com/a/35897235/33080
//    https://docs.microsoft.com/en-us/previous-versions/windows/desktop/stormgmt/msft-disk
//    https://github.com/mirror/smartmontools/blob/master/ataprint.cpp

namespace BasicSMART
{
    /// <summary>Provides methods for obtaining information about physical drives present in the system.</summary>
    public static class SMART
    {
        /// <summary>
        ///     Retrieves information about physical drives present in the system, omitting any SMART data. Requires
        ///     administrative privileges. See also <see cref="GetDrivesWithSMART"/>.</summary>
        public static List<SmartDriveInfo> GetDrives()
        {
            var drives1 = convert(new ManagementObjectSearcher(@"\\localhost\ROOT\Microsoft\Windows\Storage", "select * from MSFT_PhysicalDisk").Get());
            var drives2 = convert(new ManagementObjectSearcher("select * from Win32_DiskDrive").Get());

            var drives = new List<SmartDriveInfo>();
            foreach (var drive1 in drives1)
            {
                var id = drive1.Get("DeviceId", null)?.ToString();
                if (id == null || id == "")
                    throw new SmartReadException($"DeviceId is missing, empty or null", null);
                var drive2 = drives2.SingleOrDefault(d2 => d2.Get("Index", null)?.ToString() == id);
                if (drive2 == null)
                    throw new SmartReadException($"Could not find matching drive in Win32_DiskDrive: DeviceId={id}", null);

                var drive = new SmartDriveInfo();
                try
                {
                    drive.DeviceIndex = drive1["DeviceId"].ToString();
                    drive.DeviceId_PnP = (string) drive2["PNPDeviceID"];
                    drive.DeviceId_WinName = (string) drive2["DeviceID"];
                    drive.DeviceId_UniqueId = (string) drive1["UniqueId"];
                    drive.UniqueIdType = (UniqueIdType) (ushort) drive1["UniqueIdFormat"];
                    drive.Model = drive1["Model"].ToString();
                    drive.SerialNumber = (string) drive1["SerialNumber"];
                    drive.FirmwareVersion = (string) drive1["FirmwareVersion"];
                    drive.BusType = (BusType) (ushort) drive1["BusType"];
                    drive.MediaType = (MediaType) (ushort) drive1["MediaType"];
                    drive.Size = (ulong) drive1["Size"];
                    drive.LogicalSectorSize = (ulong) drive1["LogicalSectorSize"];
                    drive.PhysicalSectorSize = (ulong) drive1["PhysicalSectorSize"];
                    drive.Location = (string) drive1["PhysicalLocation"];
                }
                catch (Exception e)
                {
                    throw new SmartReadException("Could not parse WMI data", drive, e);
                }

                assert(drive, drive.DeviceId_WinName == (string) drive2["Name"]);
                assert(drive, drive.Model == (string) drive2["Model"]);
                assert(drive, drive.Model == (string) drive1["FriendlyName"]);
                assert(drive, drive.Model == (string) drive2["Caption"]);
                assert(drive, drive.SerialNumber == ((string) drive2["SerialNumber"]).Trim());
                assert(drive, drive.FirmwareVersion == (string) drive2["FirmwareRevision"]);
                assert(drive, drive.Size >= (ulong) drive2["Size"]); // raw drive can be a little larger
                assert(drive, drive.LogicalSectorSize == (uint) drive2["BytesPerSector"]);

                drives.Add(drive);
            }

            return drives;
        }

        /// <summary>
        ///     Retrieves information about physical drives present in the system, including SMART data. Requires
        ///     administrative privileges. See also <see cref="GetDrives"/>.</summary>
        public static List<SmartDriveInfo> GetDrivesWithSMART()
        {
            var drives = GetDrives();

            var failureStatuses = convert(new ManagementObjectSearcher(@"\root\wmi", "select * from MSStorageDriver_FailurePredictStatus").Get());
            var failureDatas = convert(new ManagementObjectSearcher(@"\root\wmi", "select * from MSStorageDriver_FailurePredictData").Get());
            var failureThresholds = convert(new ManagementObjectSearcher(@"\root\wmi", "select * from MSStorageDriver_FailurePredictThresholds").Get());

            foreach (var drive in drives)
            {
                var failureStatus = failureStatuses.SingleOrDefault(f => ((string) f["InstanceName"]).ToUpper() == drive.DeviceId_PnP + "_0");
                var failureData = failureDatas.SingleOrDefault(f => ((string) f["InstanceName"]).ToUpper() == drive.DeviceId_PnP + "_0");
                var failureThreshold = failureThresholds.SingleOrDefault(f => ((string) f["InstanceName"]).ToUpper() == drive.DeviceId_PnP + "_0");

                if (failureStatus == null || failureData == null || failureThreshold == null)
                    throw new SmartReadException($"Could not find SMART data", drive);

                drive.SmartPredictFailure = (bool) failureStatus["PredictFailure"];
                drive.SmartReadings = parseSmartData((byte[]) failureData["VendorSpecific"], (byte[]) failureThreshold["VendorSpecific"]);
            }

            return drives;
        }

        private static void assert(SmartDriveInfo drv, bool assertion)
        {
            if (!assertion)
                throw new SmartReadException("Inconsistent data", drv);
        }

        private static List<Dictionary<string, object>> convert(ManagementObjectCollection coll)
        {
            return coll.OfType<ManagementObject>().Select(obj => obj.Properties.OfType<PropertyData>().ToDictionary(pd => pd.Name, pd => pd.Value)).ToList();
        }

        private static List<SmartReading> parseSmartData(byte[] readings, byte[] thresholds)
        {
            var result = new List<SmartReading>();
            int pos = 2; // first byte is clearly attribute count on some drives, but not on others :(
            while (pos <= readings.Length - 12)
            {
                byte id = readings[pos + 0];
                if (id == 0)
                    break;
                if (result.Any(r => r.Id == id))
                    break; // we're decoding junk now
                var reading = new SmartReading();
                reading.Id = id;
                reading.Flags = readings[pos + 2];
                reading.Current = readings[pos + 3];
                reading.Worst = readings[pos + 4];
                reading.Raw = BitConverter.ToInt32(readings, pos + 5);
                result.Add(reading);

                pos += 12;
            }

            pos = 2;
            while (pos <= thresholds.Length - 12)
            {
                byte id = thresholds[pos];
                byte threshold = thresholds[pos + 1];
                pos += 12;
                var reading = result.SingleOrDefault(r => r.Id == id);
                if (reading != null)
                    reading.Threshold = threshold;
            }

            result.Sort((r1, r2) => r1.Id.CompareTo(r2.Id));
            return result;
        }

        /// <summary>Holds the names of known SMART attributes.</summary>
        public static IReadOnlyDictionary<byte, string> AttributeNames = new ReadOnlyDictionary<byte, string>(new Dictionary<byte, string>
        {
            { 0x01, "Read error rate" },
            { 0x02, "Throughput performance" },
            { 0x03, "Spin-up time" },
            { 0x04, "Start/stop count" },
            { 0x05, "Reallocated sector count" },
            { 0x07, "Seek error rate" },
            { 0x08, "Seek time performance" },
            { 0x09, "Power-on hours" },
            { 0x0A, "Spin retry count" },
            { 0x0B, "Recalibration retries" },
            { 0x0C, "Power cycle count" },
            { 0xB1, "Wear leveling count" },
            { 0xB3, "Used reserved block count" },
            { 0xB5, "Program fail count" },
            { 0xB6, "Erase fail count" },
            { 0xB7, "Runtime bad block" },
            { 0xB8, "End-to-end error" },
            { 0xBB, "Uncorrectable error count" },
            { 0xBC, "Command timeout" },
            { 0xBD, "High fly writes" },
            { 0xBE, "Airflow temperature" },
            { 0xBF, "G-sense error rate" },
            { 0xC0, "Power-off retract count" },
            { 0xC1, "Load/unload cycle count" },
            { 0xC2, "Temperature" },
            { 0xC3, "ECC error rate" },
            { 0xC4, "Reallocation event count" },
            { 0xC5, "Current pending sector count" },
            { 0xC6, "Uncorrectable sector count" },
            { 0xC7, "CRC error count" },
            { 0xC8, "Write error rate" },
            { 0xEB, "POR recovery count" },
            { 0xF0, "Head flying hours" },
            { 0xF1, "Total host writes" },
            { 0xF2, "Total host reads" },
        });
    }

    /// <summary>Generic exception from the BasicSMART library.</summary>
    public class SmartReadException : Exception
    {
        internal SmartReadException(string message, SmartDriveInfo drv)
            : base(getMessage(message, drv))
        {
        }

        internal SmartReadException(string message, SmartDriveInfo drv, Exception inner)
            : base(getMessage(message, drv), inner)
        {
        }

        private static string getMessage(string message, SmartDriveInfo drv)
        {
            if (drv == null)
                return message;
            else
                return $"{message} [DeviceIndex={drv.DeviceIndex}, Serial={drv?.SerialNumber ?? "?"}]";
        }
    }

#pragma warning disable 1591 // missing XML documentation

    public class SmartDriveInfo
    {
        public string DeviceIndex { get; set; }
        public string DeviceId_PnP { get; set; }
        public string DeviceId_WinName { get; set; }
        public string DeviceId_UniqueId { get; set; }
        public UniqueIdType UniqueIdType { get; set; }
        public string Model { get; set; }
        public string SerialNumber { get; set; }
        public string FirmwareVersion { get; set; }
        public BusType BusType { get; set; }
        public MediaType MediaType { get; set; }
        public ulong Size { get; set; }
        public ulong LogicalSectorSize { get; set; }
        public ulong PhysicalSectorSize { get; set; }
        public string Location { get; set; }

        public bool SmartPredictFailure { get; set; }
        public List<SmartReading> SmartReadings { get; set; }

        public override string ToString() => $"{MediaType}/{BusType}: {Model}, s/n: {SerialNumber}, {Size:#,0} bytes, {LogicalSectorSize}/{PhysicalSectorSize} sectors, {Location}";
    }

    public class SmartReading
    {
        public byte Id { get; set; }
        public byte Flags { get; set; }
        public byte Current { get; set; }
        public byte Worst { get; set; }
        public int Raw { get; set; }
        public byte Threshold { get; set; }

        public string Name => SMART.AttributeNames.Get(Id, "?");

        public override string ToString() => $"{Id:X2} | {Current:000} / {Worst:000} / {Threshold:000} | {Raw} | {Name}";
    }

    public enum BusType
    {
        Unknown = 0,
        SCSI = 1,
        ATAPI = 2,
        ATA = 3,
        IEEE1394 = 4,
        SSA = 5,
        FibreChannel = 6,
        USB = 7,
        RAID = 8,
        iSCSI = 9,
        SAS = 10,
        SATA = 11,
        SD = 12,
        MMC = 13,
        MAX = 14,
        FileBackedVirtual = 15,
        StorageSpaces = 16,
        NVMe = 17,
    }

    public enum MediaType
    {
        Unspecified = 0,
        HDD = 3,
        SSD = 4,
        SCM = 5,
    }

    public enum UniqueIdType
    {
        VendorSpecific = 0,
        VendorId = 1,
        EUI64 = 2,
        FCPH = 3,
        SCSI = 8,
    }

#pragma warning restore 1591 // missing XML documentation
}
