import type { StateCreator } from "zustand";
import {
  listViews,
  createView,
  updateView,
  deleteView as deleteViewApi
} from "../../api/views";
import { updateExplorerTablePreferences } from "../../api/explorerPreferences";
import type { ExplorerViewConfig } from "../../types/explorer";
import {
  DEFAULT_EXPLORER_VIEW_CONFIG,
  DEFAULT_QUERY,
  patchExplorerViewConfig
} from "../../types/explorer";
import { cancelAutoSave, getAutoSaveTarget, scheduleAutoSave } from "./autoSave";
import {
  cloneExplorerViewConfig,
  currentUserOwns,
  extractExplorerViewConfig,
  freshTableView,
  rebuildConfigForType
} from "./helpers";
import type { ExplorerStoreState, ViewSlice } from "./types";

export const initialViewState = {
  defaultViewIdByTable: {} as Record<string, number | null>,
};

export const createViewSlice: StateCreator<ExplorerStoreState, [], [], ViewSlice> = (
  set,
  get
) => ({
  ...initialViewState,

  setHiddenColumns: (table, columnNames) => {
    set((state) => {
      const view = state.views[table] ?? freshTableView();

      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...view,
            config: patchExplorerViewConfig(view.config, {
              hiddenColumnNames: Array.from(new Set(columnNames.filter(Boolean)))
            })
          }
        }
      };
    });
    // Only schedule auto-save if this is the active table
    if (get().activeTable === table) scheduleAutoSave(get);
  },

  setColumnOrder: (table, columnNames) => {
    set((state) => {
      const view = state.views[table] ?? freshTableView();

      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...view,
            config: patchExplorerViewConfig(view.config, {
              columnOrder: Array.from(new Set(columnNames.filter(Boolean)))
            })
          }
        }
      };
    });
    if (get().activeTable === table) scheduleAutoSave(get);
  },

  // ── Saved-views actions ───────────────────────────────────────────────────

  loadSavedViews: async (tableId) => {
    set((state) => {
      const view = state.views[tableId] ?? freshTableView();
      return {
        views: {
          ...state.views,
          [tableId]: { ...view, savedViewsStatus: "loading" }
        }
      };
    });

    try {
      const result = await listViews(tableId);
      const views = result.data ?? [];
      const prefs = result.preferences ?? { default_view_id: null, auto_save: false };
      set((state) => {
        const view = state.views[tableId] ?? freshTableView();
        return {
          defaultViewIdByTable: { ...state.defaultViewIdByTable, [tableId]: prefs.default_view_id },
          views: {
            ...state.views,
            [tableId]: { ...view, savedViews: views, savedViewsStatus: "ready", autoSave: prefs.auto_save }
          }
        };
      });
    } catch {
      set((state) => {
        const view = state.views[tableId] ?? freshTableView();
        return {
          views: {
            ...state.views,
            [tableId]: { ...view, savedViewsStatus: "error" }
          }
        };
      });
    }
  },

  switchView: (viewId) => {
    cancelAutoSave(getAutoSaveTarget(get));

    const { activeTable, views, fetchRows } = get();
    if (!activeTable) return;

    const view = views[activeTable] ?? freshTableView();

    let newConfig: ExplorerViewConfig;
    if (viewId === null) {
      // "All Records" — reset to defaults
      newConfig = cloneExplorerViewConfig(DEFAULT_EXPLORER_VIEW_CONFIG);
    } else {
      const saved = view.savedViews.find((v) => v.id === viewId);
      if (!saved) return; // unknown id — ignore
      // Clone the saved config and force config.type to match the
      // saved view's top-level type — they can drift if the config was
      // saved while on a different view type.
      newConfig = cloneExplorerViewConfig(saved.config);
      if (newConfig.type !== saved.type) {
        newConfig = rebuildConfigForType(newConfig, saved.type);
      }
    }

    set((state) => ({
      views: {
        ...state.views,
        [activeTable]: {
          ...view,
          config: newConfig,
          activeViewId: viewId,
          page: DEFAULT_QUERY.page,
          selectedRowIds: [],
          context: null,
          rows: [],
          meta: null,
          error: null,
          status: "idle"
        }
      }
    }));

    void fetchRows();
  },

  saveCurrentAsView: async (name, type, isShared) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = views[activeTable] ?? freshTableView();
    let config = extractExplorerViewConfig(view);
    // Ensure config.type matches the requested type so the
    // persisted config JSON and the saved-view type agree.
    if (config.type !== type) {
      config = rebuildConfigForType(config, type);
    }

    const created = await createView({
      name,
      table_name: activeTable,
      type,
      config,
      is_shared: isShared
    });

    set((state) => {
      const v = state.views[activeTable] ?? freshTableView();
      return {
        views: {
          ...state.views,
          [activeTable]: {
            ...v,
            savedViews: [...v.savedViews, created],
            activeViewId: created.id,
            // If the user created a view with a different type than the
            // current runtime config (now possible via the create modal),
            // re-hydrate the runtime config to match the created view.
            config: cloneExplorerViewConfig(created.config),
          }
        }
      };
    });
  },

  updateActiveViewConfig: async () => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = views[activeTable];
    if (!view || view.activeViewId === null) return;

    await get().updateViewConfigFor(activeTable, view.activeViewId);
  },

  updateViewConfigFor: async (tableId, viewId) => {
    const { views } = get();
    const view = views[tableId];
    if (!view) return;
    if (view.activeViewId !== viewId) return;

    const target = view.savedViews.find((sv) => sv.id === viewId);
    if (!currentUserOwns(target)) return;

    const config = extractExplorerViewConfig(view);
    const updated = await updateView(viewId, { config });

    set((state) => {
      const v = state.views[tableId];
      if (!v) return state;
      return {
        views: {
          ...state.views,
          [tableId]: {
            ...v,
            savedViews: v.savedViews.map((sv) =>
              sv.id === updated.id ? updated : sv
            )
          }
        }
      };
    });
  },

  renameView: async (id, name) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const target = views[activeTable]?.savedViews.find((sv) => sv.id === id);
    if (!currentUserOwns(target)) return;

    const updated = await updateView(id, { name });

    set((state) => {
      const v = state.views[activeTable];
      if (!v) return state;
      return {
        views: {
          ...state.views,
          [activeTable]: {
            ...v,
            savedViews: v.savedViews.map((sv) =>
              sv.id === updated.id ? updated : sv
            )
          }
        }
      };
    });
  },

  toggleViewShared: async (id) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = views[activeTable];
    const target = view?.savedViews.find((sv) => sv.id === id);
    if (!target) return;
    if (!currentUserOwns(target)) return;

    const updated = await updateView(id, { is_shared: !target.is_shared });

    set((state) => {
      const v = state.views[activeTable];
      if (!v) return state;
      return {
        views: {
          ...state.views,
          [activeTable]: {
            ...v,
            savedViews: v.savedViews.map((sv) =>
              sv.id === updated.id ? updated : sv
            )
          }
        }
      };
    });
  },

  deleteView: async (id) => {
    const { activeTable, views, switchView } = get();
    if (!activeTable) return;

    const target = views[activeTable]?.savedViews.find((sv) => sv.id === id);
    if (!currentUserOwns(target)) return;

    await deleteViewApi(id);

    const wasActive = views[activeTable]?.activeViewId === id;

    set((state) => {
      const v = state.views[activeTable];
      if (!v) return state;
      return {
        views: {
          ...state.views,
          [activeTable]: {
            ...v,
            savedViews: v.savedViews.filter((sv) => sv.id !== id)
          }
        }
      };
    });

    if (wasActive) {
      switchView(null);
    }
  },

  changeViewType: async (id, type) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = views[activeTable];
    const target = view?.savedViews.find((sv) => sv.id === id);
    if (!target) return;
    if (!currentUserOwns(target)) return;

    // Re-shape the config for the new type. patchExplorerViewConfig can't
    // change the type itself, so we build the new config by hand.
    const base = {
      search: target.config.search,
      sort: target.config.sort.map((s) => ({ ...s })),
      filters: target.config.filters.map((f) => ({ ...f })),
      hiddenColumnNames: [...target.config.hiddenColumnNames],
      columnOrder: [...target.config.columnOrder]
    };
    const nextConfig: ExplorerViewConfig =
      type === "kanban"
        ? { ...base, type: "kanban", kanbanColumn: null, cardFieldNames: [], hiddenLaneValues: [] }
        : { ...base, type: "table" };

    const updated = await updateView(id, { type, config: nextConfig });

    set((state) => {
      const v = state.views[activeTable];
      if (!v) return state;
      const nextSavedViews = v.savedViews.map((sv) =>
        sv.id === updated.id ? updated : sv
      );
      // If this is the active view, re-hydrate its config so the UI reflects
      // the new type without a full re-switch.
      const nextView =
        v.activeViewId === id
          ? { ...v, savedViews: nextSavedViews, config: cloneExplorerViewConfig(updated.config) }
          : { ...v, savedViews: nextSavedViews };
      return {
        views: {
          ...state.views,
          [activeTable]: nextView
        }
      };
    });
  },

  duplicateView: async (id, name) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = views[activeTable];
    const source = view?.savedViews.find((sv) => sv.id === id);
    if (!source) return;

    // Clone the source config so edits to the copy don't bleed back.
    const configCopy = cloneExplorerViewConfig(source.config);

    const created = await createView({
      name,
      table_name: activeTable,
      type: source.type,
      config: configCopy,
      is_shared: false
    });

    set((state) => {
      const v = state.views[activeTable] ?? freshTableView();
      return {
        views: {
          ...state.views,
          [activeTable]: {
            ...v,
            savedViews: [...v.savedViews, created],
            config: cloneExplorerViewConfig(created.config),
            activeViewId: created.id
          }
        }
      };
    });
  },

  setDefaultView: async (id) => {
    const { activeTable } = get();
    if (!activeTable) return;
    set((state) => {
      const next = { ...state.defaultViewIdByTable, [activeTable]: id };
      return { defaultViewIdByTable: next };
    });

    // Persist per-user so the default follows the account across browsers.
    try {
      await updateExplorerTablePreferences(activeTable, { default_view_id: id });
    } catch {
      // If the backend can't be reached, keep the in-memory preference.
    }
  },

  setAutoSave: (enabled) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    // Only allow enabling auto-save on views the current user owns. Turning
    // it off is always allowed (safety release valve).
    if (enabled) {
      const v = views[activeTable];
      const target = v?.savedViews.find((sv) => sv.id === v.activeViewId);
      if (!currentUserOwns(target)) return;
    }

    set((state) => {
      const v = state.views[activeTable] ?? freshTableView();
      return {
        views: {
          ...state.views,
          [activeTable]: { ...v, autoSave: enabled }
        }
      };
    });

    // Persist per-user so the toggle survives refreshes and other browsers.
    void updateExplorerTablePreferences(activeTable, { auto_save: enabled }).catch(() => {
      /* ignore */
    });

    // Immediately persist if turning on with an active saved view
    if (enabled) {
      const view = views[activeTable];
      if (view?.activeViewId !== null) {
        void get().updateActiveViewConfig();
      }
    }
  },

  setKanbanConfig: (patch) => {
    const { activeTable, views } = get();
    if (!activeTable) return;
    const view = views[activeTable];
    if (!view) return;
    if (view.config.type !== "kanban") return;

    const nextConfig: ExplorerViewConfig = {
      ...view.config,
      kanbanColumn:
        patch.kanbanColumn !== undefined
          ? patch.kanbanColumn
          : view.config.kanbanColumn,
      cardFieldNames: patch.cardFieldNames
        ? [...patch.cardFieldNames].slice(0, 3)
        : [...view.config.cardFieldNames],
      hiddenLaneValues: patch.hiddenLaneValues
        ? [...patch.hiddenLaneValues]
        : [...(view.config.hiddenLaneValues ?? [])]
    };

    set((state) => ({
      views: {
        ...state.views,
        [activeTable]: { ...view, config: nextConfig }
      }
    }));

    scheduleAutoSave(get);
  },
});
