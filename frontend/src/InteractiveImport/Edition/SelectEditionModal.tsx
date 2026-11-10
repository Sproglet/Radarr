import React, { useCallback } from 'react';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import { kinds } from 'Helpers/Props';
import MovieEdition from 'Movie/MovieEdition';
import translate from 'Utilities/String/translate';

const columns = [
  { name: 'name', label: () => translate('Edition'), isVisible: true },
  {
    name: 'profile',
    label: () => translate('QualityProfile'),
    isVisible: true,
  },
  { name: 'file', label: () => translate('File'), isVisible: true },
];

interface SelectEditionModalProps {
  isOpen: boolean;
  movieId: number;
  movieEditionId?: number;
  onEditionSelect(edition: MovieEdition): void;
  onModalClose(): void;
}

function EditionChoice({
  edition,
  isSelected,
  onEditionSelect,
}: {
  edition: MovieEdition;
  isSelected: boolean;
  onEditionSelect(edition: MovieEdition): void;
}) {
  const onSelect = useCallback(
    () => onEditionSelect(edition),
    [edition, onEditionSelect]
  );
  return (
    <TableRow>
      <TableRowCell>
        <Button
          kind={isSelected ? kinds.PRIMARY : kinds.DEFAULT}
          isDisabled={edition.state === 'invalid'}
          title={edition.rejectionReason}
          aria-pressed={isSelected}
          onPress={onSelect}
        >
          {edition.name}
        </Button>
      </TableRowCell>
      <TableRowCell>{edition.qualityProfileName}</TableRowCell>
      <TableRowCell>
        {edition.state === 'invalid'
          ? translate('EditionInvalid')
          : edition.relativePath ?? translate('EditionMissing')}
      </TableRowCell>
    </TableRow>
  );
}

function SelectEditionContent({
  movieId,
  movieEditionId,
  onEditionSelect,
  onModalClose,
}: Omit<SelectEditionModalProps, 'isOpen'>) {
  const { data, isPending, error } = useApiQuery<MovieEdition[]>({
    path: `/movieedition?movieId=${movieId}`,
    queryOptions: { staleTime: 0, refetchOnMount: 'always' },
  });

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>{translate('SelectEdition')}</ModalHeader>
      <ModalBody>
        <p>{translate('SelectEditionHelpText')}</p>
        {isPending ? <LoadingIndicator /> : null}
        {error ? (
          <Alert kind={kinds.DANGER}>{translate('UnableToLoadEditions')}</Alert>
        ) : null}
        {data?.length ? (
          <Table columns={columns}>
            <TableBody>
              {data.map((edition) => (
                <EditionChoice
                  key={edition.id}
                  edition={edition}
                  isSelected={edition.id === movieEditionId}
                  onEditionSelect={onEditionSelect}
                />
              ))}
            </TableBody>
          </Table>
        ) : null}
        {data && !data.length ? (
          <Alert>{translate('NoEditionsConfigured')}</Alert>
        ) : null}
      </ModalBody>
      <ModalFooter>
        <Button onPress={onModalClose}>{translate('Cancel')}</Button>
      </ModalFooter>
    </ModalContent>
  );
}

function SelectEditionModal({ isOpen, ...props }: SelectEditionModalProps) {
  return (
    <Modal isOpen={isOpen} onModalClose={props.onModalClose}>
      <SelectEditionContent {...props} />
    </Modal>
  );
}

export default SelectEditionModal;
