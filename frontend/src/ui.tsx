import { parseDate } from '@internationalized/date'
import { Link as RouterLink } from '@tanstack/react-router'
import type { AnchorHTMLAttributes, ComponentProps, MouseEvent, ReactNode } from 'react'
import {
  Button as AriaButton,
  Link as AriaLink,
  Calendar,
  CalendarCell,
  CalendarGrid,
  CalendarGridBody,
  CalendarGridHeader,
  CalendarHeaderCell,
  CalendarHeading,
  CheckboxButton,
  CheckboxField,
  DateInput,
  DatePicker,
  DateSegment,
  Dialog,
  FieldError,
  Group,
  Heading,
  Input,
  Label,
  ListBox,
  ListBoxItem,
  Modal,
  ModalOverlay,
  Popover,
  Select,
  SelectValue,
  TextField as AriaTextField,
} from 'react-aria-components'
import { ApiError } from './api'

export function errorText(error: unknown): string {
  if (!error) {
    return ''
  }

  if (typeof error === 'string') {
    return error
  }

  if (error instanceof ApiError) {
    return error.errors.map((item) => item.message).join(' ')
  }

  if (error instanceof Error) {
    return error.message
  }

  if (Array.isArray(error)) {
    return error.map((item) => errorText(item)).filter(Boolean).join(' ')
  }

  if (typeof error === 'object' && 'message' in error) {
    const message = (error as { message: unknown }).message
    if (typeof message === 'string') {
      return message
    }
  }

  return 'The request failed.'
}

export function ErrorText({ error }: { error: unknown }) {
  const text = errorText(error)
  if (!text) {
    return null
  }

  return (
    <p className="field-error" role="alert">
      {text}
    </p>
  )
}

export function Page({ title, action, children }: { title: string; action?: ReactNode; children: ReactNode }) {
  return (
    <section className="page">
      <div className="page-head">
        <h1 className="page-title">{title}</h1>
        {action}
      </div>
      {children}
    </section>
  )
}

export function Button({
  children,
  onPress,
  type = 'button',
  isDisabled,
  quiet,
  slot,
  label,
  to,
}: {
  children: ReactNode
  onPress?: () => void
  type?: 'button' | 'submit' | 'reset'
  isDisabled?: boolean
  quiet?: boolean
  slot?: 'previous' | 'next'
  label?: string
  to?: ComponentProps<typeof RouterLink>['to']
}) {
  const className = 'react-aria-Button button-base'
  const variant = quiet ? 'quiet' : 'primary'
  if (to) {
    return (
      <AriaLink
        className={className}
        data-variant={variant}
        aria-label={label}
        render={(domProps) => {
          const props = domProps as AnchorHTMLAttributes<HTMLAnchorElement>
          const { href: _href, children: _children, onClick, ...rest } = props
          return (
            <RouterLink
              to={to}
              {...(rest as Omit<ComponentProps<typeof RouterLink>, 'to'>)}
              onClick={(event: MouseEvent<HTMLAnchorElement>) => {
                onClick?.(event)
              }}
            >
              {children}
            </RouterLink>
          )
        }}
      >
        {children}
      </AriaLink>
    )
  }

  return (
    <AriaButton
      type={type}
      onPress={onPress}
      isDisabled={isDisabled}
      slot={slot}
      aria-label={label}
      className={className}
      data-variant={variant}
    >
      {children}
    </AriaButton>
  )
}

export function TextField({
  label,
  value,
  onChange,
  onBlur,
  type = 'text',
  autoComplete,
  error,
  isReadOnly,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  onBlur?: () => void
  type?: 'text' | 'email' | 'password'
  autoComplete?: string
  error?: string
  isReadOnly?: boolean
}) {
  return (
    <AriaTextField
      value={value}
      onChange={onChange}
      onBlur={onBlur}
      type={type}
      autoComplete={autoComplete}
      isReadOnly={isReadOnly}
      isInvalid={Boolean(error)}
    >
      <Label>{label}</Label>
      <Input className="react-aria-Input inset" />
      {error ? <FieldError>{error}</FieldError> : null}
    </AriaTextField>
  )
}

function ChevronDown() {
  return (
    <svg className="lucide-chevron-down" viewBox="0 0 24 24" aria-hidden="true">
      <path
        d="m6 9 6 6 6-6"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  )
}

function ChevronLeft() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path
        d="m15 18-6-6 6-6"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  )
}

function ChevronRight() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path
        d="m9 18 6-6-6-6"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  )
}

function CheckMark() {
  return (
    <svg className="lucide-check" viewBox="0 0 24 24" aria-hidden="true">
      <path
        d="M20 6 9 17l-5-5"
        fill="none"
        stroke="currentColor"
        strokeWidth="3"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  )
}

export function Choice({
  label,
  value,
  onChange,
  options,
  error,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  options: { id: string; label: string }[]
  error?: string
}) {
  return (
    <Select
      selectedKey={value || null}
      onSelectionChange={(key) => onChange(key == null ? '' : String(key))}
      isInvalid={Boolean(error)}
    >
      <Label>{label}</Label>
      <Button>
        <SelectValue />
        <ChevronDown />
      </Button>
      {error ? <FieldError>{error}</FieldError> : null}
      <Popover className="react-aria-Popover select-popover">
        <ListBox className="dropdown-listbox">
          {options.map((option) => (
            <ListBoxItem key={option.id} id={option.id} className="dropdown-item" textValue={option.label}>
              {({ isSelected }) => (
                <>
                  {isSelected ? <CheckMark /> : null}
                  <span slot="label">{option.label}</span>
                </>
              )}
            </ListBoxItem>
          ))}
        </ListBox>
      </Popover>
    </Select>
  )
}

export function DateField({
  label,
  value,
  onChange,
  onFocusDate,
  error,
}: {
  label: string
  value: string
  onChange: (iso: string) => void
  onFocusDate?: (iso: string) => void
  error?: string
}) {
  return (
    <DatePicker
      granularity="day"
      value={value ? parseDate(value) : null}
      onChange={(next) => {
        const iso = next ? next.toString() : ''
        onChange(iso)
        if (iso) {
          onFocusDate?.(iso)
        }
      }}
      isInvalid={Boolean(error)}
    >
      <Label>{label}</Label>
      <Group>
        <DateInput className="react-aria-DateInput inset">
          {(segment) => <DateSegment segment={segment} />}
        </DateInput>
        <AriaButton className="field-Button" aria-label="Open calendar">
          <ChevronDown />
        </AriaButton>
      </Group>
      {error ? <FieldError>{error}</FieldError> : null}
      <Popover className="react-aria-Popover">
        <Calendar>
          <div className="months">
            <div className="month">
              <header>
                <Button quiet slot="previous">
                  <ChevronLeft />
                </Button>
                <CalendarHeading />
                <Button quiet slot="next">
                  <ChevronRight />
                </Button>
              </header>
              <CalendarGrid>
                <CalendarGridHeader>
                  {(day) => <CalendarHeaderCell>{day}</CalendarHeaderCell>}
                </CalendarGridHeader>
                <CalendarGridBody>
                  {(date) => (
                    <CalendarCell
                      date={date}
                      className="react-aria-CalendarCell button-base"
                      data-variant="quiet"
                      onHoverStart={() => onFocusDate?.(date.toString())}
                    />
                  )}
                </CalendarGridBody>
              </CalendarGrid>
            </div>
          </div>
        </Calendar>
      </Popover>
    </DatePicker>
  )
}

export function Check({
  label,
  isSelected,
  onChange,
}: {
  label: string
  isSelected: boolean
  onChange: (value: boolean) => void
}) {
  return (
    <CheckboxField isSelected={isSelected} onChange={onChange}>
      <CheckboxButton>
        {({ isIndeterminate }) => (
          <>
            <div className="indicator">
              <svg viewBox="0 0 18 18" aria-hidden="true" key={isIndeterminate ? 'indeterminate' : 'check'}>
                {isIndeterminate ? <rect x={1} y={7.5} width={16} height={3} /> : <polyline points="2 9 7 14 16 4" />}
              </svg>
            </div>
            {label}
          </>
        )}
      </CheckboxButton>
    </CheckboxField>
  )
}

export function Prompt({
  open,
  onOpenChange,
  title,
  children,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: string
  children: ReactNode
}) {
  return (
    <ModalOverlay isOpen={open} onOpenChange={onOpenChange} isDismissable>
      <Modal>
        <Dialog className="admin-dialog">
          <Heading slot="title">{title}</Heading>
          <div className="page">{children}</div>
        </Dialog>
      </Modal>
    </ModalOverlay>
  )
}

export function fieldErrors(errors: ReadonlyArray<unknown>): string {
  return errors.map((error) => errorText(error)).filter(Boolean).join(' ')
}
