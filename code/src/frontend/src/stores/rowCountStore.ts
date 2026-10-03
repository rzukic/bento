import { create } from "zustand";

/**
 * Last known unfiltered row count per table. Filled by whoever already has
 * the number (the Home page's counts, an unfiltered table view's total) so
 * the table tabs can show counts without requests of their own.
 */
type RowCountState = {
  counts: Record<string, number>;
  setCount: (table: string, count: number) => void;
};

export const useRowCountStore = create<RowCountState>()((set) => ({
  counts: {},
  setCount: (table, count) =>
    set((state) => (state.counts[table] === count ? state : { counts: { ...state.counts, [table]: count } })),
}));
