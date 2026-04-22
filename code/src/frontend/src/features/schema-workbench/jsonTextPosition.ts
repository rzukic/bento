export type JsonTextLocation = {
  line: number;
  column: number;
};

export function locationFromIndex(text: string, index: number): JsonTextLocation {
  let line = 1;
  let column = 1;

  for (let offset = 0; offset < index && offset < text.length; offset += 1) {
    if (text[offset] === "\n") {
      line += 1;
      column = 1;
      continue;
    }

    column += 1;
  }

  return { line, column };
}

export function indexFromLocation(text: string, location: JsonTextLocation): number {
  const targetLine = Math.max(1, location.line);
  const targetColumn = Math.max(1, location.column);
  let line = 1;
  let column = 1;

  for (let index = 0; index < text.length; index += 1) {
    if (line === targetLine && column === targetColumn) {
      return index;
    }

    if (text[index] === "\n") {
      line += 1;
      column = 1;
    } else {
      column += 1;
    }
  }

  return text.length;
}
