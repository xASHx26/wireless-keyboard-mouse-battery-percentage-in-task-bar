using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PeripheralBattery
{
    internal sealed class Peripheral
    {
        public string Id;
        public string Name;
        public string Kind;
        public string Connection;
        public int? Level;

        public bool LastReported;

        public string Status
        {
            get { return Level.HasValue ? Level.Value + "%" + (LastReported ? " (last reported)" : "") : "Battery level unavailable"; }
        }
    }

    internal static class DeviceReader
    {
        private static readonly Dictionary<string, int> ReceiverLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Guid ComputerContainer = new Guid("00000000-0000-0000-ffff-ffffffffffff");
        private static readonly Guid MouseClass = new Guid("4d36e96f-e325-11ce-bfc1-08002be10318");
        private static readonly Guid KeyboardClass = new Guid("4d36e96b-e325-11ce-bfc1-08002be10318");
        private static readonly PropertyKey Class = new PropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 10);
        private static readonly PropertyKey Name = new PropertyKey("b725f130-47ef-101a-a5f1-02608c9eebac", 10);
        private static readonly PropertyKey FriendlyName = new PropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        private static readonly PropertyKey Model = new PropertyKey("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
        private static readonly PropertyKey Container = new PropertyKey("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
        // System.Devices.BatteryLife, in percent when the driver supplies it.
        private static readonly PropertyKey Battery = new PropertyKey("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 2);

        private sealed class Node
        {
            public uint Handle;
            public Guid ContainerId;
            public Guid SetupClass;
            public string Id;
            public int? Level;
        }

        public static List<Peripheral> Read()
        {
            var nodes = new Dictionary<uint, Node>();
            foreach (string id in PresentDeviceIds())
            {
                uint handle;
                if (CM_Locate_DevNode(out handle, id, 0) != 0) continue;
                uint status;
                uint problem;
                if (CM_Get_DevNode_Status(out status, out problem, handle, 0) != 0 ||
                    (status & 0x02000000) != 0) continue;

                nodes[handle] = new Node
                {
                    Handle = handle,
                    Id = id,
                    SetupClass = GuidProperty(handle, Class),
                    ContainerId = GuidProperty(handle, Container),
                    Level = BatteryLevel(handle)
                };
            }

            var containers = nodes.Values.Where(n => HasOwnContainer(n.ContainerId))
                .ToLookup(n => n.ContainerId);
            var results = new List<Peripheral>();

            var inputs = nodes.Values.Where(n => n.SetupClass == MouseClass || n.SetupClass == KeyboardClass);
            foreach (var group in inputs.GroupBy(n => new
            {
                n.SetupClass,
                Identity = HasOwnContainer(n.ContainerId) ? n.ContainerId.ToString() : n.Id
            }))
            {
                var members = group.SelectMany(n => Ancestors(n, nodes, containers)).Distinct().ToList();
                var bluetooth = members.FirstOrDefault(n => n.Id.StartsWith("BTHLE\\DEV_", StringComparison.OrdinalIgnoreCase) ||
                    n.Id.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase));
                var usb = members.FirstOrDefault(n => n.Id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase));
                var preferred = bluetooth ?? usb ?? group.First();
                var level = members.Where(n => n.Level.HasValue).Select(n => n.Level).Min();

                string name = StringProperty(preferred.Handle, FriendlyName) ?? StringProperty(preferred.Handle, Model) ??
                    StringProperty(preferred.Handle, Name) ?? StringProperty(group.First().Handle, Name) ?? "Input device";
                bool isMouse = group.Key.SetupClass == MouseClass;
                bool knownMouseReceiver = usb != null && (ReceiverBattery.IsRongyuanMouse(usb.Id) || ReceiverBattery.IsNacodexMouse(usb.Id));
                bool isNacodexReceiver = usb != null && ReceiverBattery.IsNacodexMouse(usb.Id);
                // These models expose an extra HID collection for shortcuts or pointer controls.
                if (knownMouseReceiver && !isMouse) continue;
                if (bluetooth != null && isMouse && name.StartsWith("AK820 ", StringComparison.OrdinalIgnoreCase))
                    continue;
                bool lastReported = false;
                if (knownMouseReceiver && !level.HasValue)
                {
                    foreach (Node member in members)
                    {
                        level = isNacodexReceiver
                            ? ReceiverBattery.ReadNacodexMouse(member.Id, out lastReported)
                            : ReceiverBattery.ReadRongyuanMouse(member.Id, out lastReported);
                        if (level.HasValue) break;
                    }
                }

                if (knownMouseReceiver)
                {
                    string receiverId = usb.ContainerId == Guid.Empty || usb.ContainerId == ComputerContainer
                        ? usb.Id : usb.ContainerId.ToString();
                    int previous;
                    if (level.HasValue)
                        ReceiverLevels[receiverId] = level.Value;
                    else if (ReceiverLevels.TryGetValue(receiverId, out previous))
                    {
                        level = previous;
                        lastReported = true;
                    }
                }

                results.Add(new Peripheral
                {
                    Id = group.Key.Identity,
                    Kind = group.Key.SetupClass == MouseClass ? "Mouse" : "Keyboard",
                    Name = name,
                    Connection = bluetooth != null ? "Bluetooth" : knownMouseReceiver ? "2.4 GHz receiver" :
                        usb != null ? "USB / receiver" : "Other",
                    LastReported = lastReported,
                    Level = level
                });
            }

            return results.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
        }

        private static IEnumerable<Node> Ancestors(Node input, Dictionary<uint, Node> nodes,
            ILookup<Guid, Node> containers)
        {
            Node current = input;
            // Stop before shared USB hubs, Bluetooth radios, and system buses.
            while (true)
            {
                yield return current;
                if (HasOwnContainer(current.ContainerId))
                {
                    foreach (Node member in containers[current.ContainerId]) yield return member;
                }

                uint parent;
                Node next;
                if (CM_Get_Parent(out parent, current.Handle, 0) != 0 || !nodes.TryGetValue(parent, out next))
                    yield break;
                if (!(next.Id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) ||
                    next.Id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase) ||
                    next.Id.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase) ||
                    next.Id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)))
                    yield break;
                if (HasOwnContainer(current.ContainerId) && next.ContainerId != current.ContainerId)
                    yield break;
                current = next;
            }
        }

        private static bool HasOwnContainer(Guid id)
        {
            return id != Guid.Empty && id != ComputerContainer;
        }

        private static string[] PresentDeviceIds()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint length;
                if (CM_Get_Device_ID_List_Size(out length, null, 0x100) != 0) break;
                var buffer = new char[length];
                uint result = CM_Get_Device_ID_List(null, buffer, length, 0x100);
                if (result == 0) return new string(buffer).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                if (result != 0x1A) break;
            }
            throw new Win32Exception("Windows could not enumerate connected devices.");
        }

        private static byte[] ReadProperty(uint node, PropertyKey key, out uint type)
        {
            var buffer = new byte[512];
            uint length = (uint)buffer.Length;
            uint result = CM_Get_DevNode_Property(node, ref key, out type, buffer, ref length, 0);
            if (result == 0x1A && length <= 65536)
            {
                buffer = new byte[length];
                result = CM_Get_DevNode_Property(node, ref key, out type, buffer, ref length, 0);
            }
            if (result != 0) return null;
            Array.Resize(ref buffer, (int)length);
            return buffer;
        }

        private static Guid GuidProperty(uint node, PropertyKey key)
        {
            uint type;
            byte[] data = ReadProperty(node, key, out type);
            return type == 13 && data != null && data.Length == 16 ? new Guid(data) : Guid.Empty;
        }

        private static string StringProperty(uint node, PropertyKey key)
        {
            uint type;
            byte[] data = ReadProperty(node, key, out type);
            if (type != 18 || data == null) return null;
            string value = Encoding.Unicode.GetString(data).TrimEnd('\0').Trim();
            return value.Length > 0 ? value : null;
        }

        private static int? BatteryLevel(uint node)
        {
            uint type;
            byte[] data = ReadProperty(node, Battery, out type);
            if (data == null) return null;
            if (type == 3 && data.Length == 1 && data[0] <= 100) return data[0];
            if (type == 7 && data.Length == 4)
            {
                uint value = BitConverter.ToUInt32(data, 0);
                if (value <= 100) return (int)value;
            }
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid Category;
            public uint Id;

            public PropertyKey(string category, uint id)
            {
                Category = new Guid(category);
                Id = id;
            }
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID_List(string filter, [Out] char[] buffer, uint length, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_DevNode_Property(uint node, ref PropertyKey key, out uint type,
            [Out] byte[] buffer, ref uint length, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    }
}





