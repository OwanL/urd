import type { ButtonHTMLAttributes } from "preact";
import { Tooltip } from "../../controls/Tooltip.tsx";
import { BrowserIcon } from "./BrowserIcon.tsx";
import type { BrowserIconName } from "./BrowserIcon.tsx";
import "../../controls/icon-button.css";

interface BrowserIconButtonProps extends Omit<
  ButtonHTMLAttributes<HTMLButtonElement>,
  "title" | "children" | "aria-label"
> {
  icon: BrowserIconName;
  label: string;
}

export function BrowserIconButton({
  icon,
  label,
  class: className,
  ...props
}: BrowserIconButtonProps) {
  return (
    <Tooltip content={label}>
      <button type="button" {...props} class={`icon-button ${className ?? ""}`} aria-label={label}>
        <BrowserIcon name={icon} />
      </button>
    </Tooltip>
  );
}
