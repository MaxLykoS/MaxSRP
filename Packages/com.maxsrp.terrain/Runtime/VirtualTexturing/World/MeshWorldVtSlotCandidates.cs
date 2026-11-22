using System;

namespace MaxTerrain
{
    /// <summary>
    /// A reusable ordering of eligible physical slots for one production plan.
    /// Empty slots precede residents; residents use the existing LRU frame and
    /// slot-index tie break. Peeking never reserves a slot or changes its age.
    /// </summary>
    internal sealed class MeshWorldVtSlotCandidates
    {
        struct Candidate
        {
            public int Slot, LastVisibleFrame;
            public bool Empty;
        }

        static int Compare(Candidate a, Candidate b)
        {
            if (a.Empty != b.Empty) return a.Empty ? -1 : 1;
            if (!a.Empty)
            {
                int age = a.LastVisibleFrame.CompareTo(b.LastVisibleFrame);
                if (age != 0) return age;
            }
            return a.Slot.CompareTo(b.Slot);
        }

        readonly Candidate[] candidates;
        readonly bool[] eligible;
        int count, remaining;
        bool built;

        public MeshWorldVtSlotCandidates(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            candidates = new Candidate[capacity];
            eligible = new bool[capacity];
        }

        public int Count => remaining;

        public void Reset()
        {
            Array.Clear(eligible, 0, eligible.Length);
            count = remaining = 0;
            built = false;
        }

        public void Add(int slot, bool isEmpty, int lastVisibleFrame)
        {
            if (built) throw new InvalidOperationException("Reset the slot candidates before adding a new plan.");
            if ((uint)slot >= (uint)eligible.Length) throw new ArgumentOutOfRangeException(nameof(slot));
            if (eligible[slot]) throw new InvalidOperationException("The slot is already a candidate.");
            candidates[count++] = new Candidate { Slot = slot, Empty = isEmpty, LastVisibleFrame = lastVisibleFrame };
            eligible[slot] = true;
            remaining++;
        }

        public void FinishBuild()
        {
            // Linear heap construction avoids sorting every candidate and avoids
            // the temporary comparison delegates used by some Array.Sort runtimes.
            for (int index = count / 2 - 1; index >= 0; index--) SiftDown(index);
            built = true;
        }

        public bool Peek(out int slot)
        {
            if (!built) throw new InvalidOperationException("Finish building slot candidates before selecting one.");
            while (count > 0 && !eligible[candidates[0].Slot])
            {
                count--;
                if (count > 0)
                {
                    candidates[0] = candidates[count];
                    SiftDown(0);
                }
            }
            slot = count > 0 ? candidates[0].Slot : -1;
            return slot >= 0;
        }

        void SiftDown(int index)
        {
            Candidate value = candidates[index];
            int child;
            while ((child = index * 2 + 1) < count)
            {
                if (child + 1 < count && Compare(candidates[child + 1], candidates[child]) < 0) child++;
                if (Compare(value, candidates[child]) <= 0) break;
                candidates[index] = candidates[child];
                index = child;
            }
            candidates[index] = value;
        }

        /// <summary>
        /// Call only after the selected slot has been reserved or published.
        /// If retiring a page fails before replacement, rebuild the candidates:
        /// the newly empty slot must regain its original empty-slot priority.
        /// </summary>
        public void Remove(int slot)
        {
            if ((uint)slot >= (uint)eligible.Length) throw new ArgumentOutOfRangeException(nameof(slot));
            if (!eligible[slot]) return;
            eligible[slot] = false;
            remaining--;
        }
    }
}
