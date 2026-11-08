import type { ButtonHTMLAttributes, ComponentChildren } from "preact";
import { Icon } from "./Icon.tsx";
import type { IconName } from "./Icon.tsx";
import { Tooltip } from "./Tooltip.tsx";
import "./icon-button.css";
import type { PopoverPlacement } from "./PopoverGeometry.ts";

interface IconButtonProps extends Omit<
  ButtonHTMLAttributes<HTMLButtonElement>,
  "title" | "children" | "aria-label"
> {
  icon: IconName;
  label: string;
  hint?: ComponentChildren;
  placement?: PopoverPlacement;
}

export function IconButton({
  icon,
  label,
  hint,
  placement = "above",
  class: className,
  ...props
}: IconButtonProps) {
  return (
    <Tooltip content={hint ?? label} placement={placement}>
      <button type="button" {...props} class={`icon-button ${className ?? ""}`} aria-label={label}>
        <Icon name={icon} />
      </button>
    </Tooltip>
  );
}
