import type { ModelColumn } from "../../../../types/api";
import { BooleanCellEditor } from "./BooleanCellEditor";
import { DateCellEditor } from "./DateCellEditor";
import { DateTimeCellEditor } from "./DateTimeCellEditor";
import { EnumCellEditor } from "./EnumCellEditor";
import { FloatCellEditor } from "./FloatCellEditor";
import { IntegerCellEditor } from "./IntegerCellEditor";
import { JsonCellEditor } from "./JsonCellEditor";
import { ReferenceCellEditor } from "./ReferenceCellEditor";
import { TextCellEditor } from "./TextCellEditor";

/**
 * The inline editor for a column, shared by the grid and the record editor.
 * Currency and decimal columns use the plain decimal editor: their static
 * value is the bare number, so the editor shows the same text.
 */
export function pickEditorForColumn(column: ModelColumn) {
  if (column.references || column.type === "relation") return ReferenceCellEditor;
  switch (column.type) {
    case "boolean":
      return BooleanCellEditor;
    case "date":
      return DateCellEditor;
    case "datetime":
      return DateTimeCellEditor;
    case "enum":
      return EnumCellEditor;
    case "json":
      return JsonCellEditor;
    case "double":
    case "float":
    case "decimal":
    case "currency":
      return FloatCellEditor;
    case "int64":
    case "integer":
    case "number":
      return IntegerCellEditor;
    default:
      return TextCellEditor;
  }
}
